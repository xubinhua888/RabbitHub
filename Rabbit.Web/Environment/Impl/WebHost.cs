﻿using Rabbit.Kernel;
using Rabbit.Kernel.Caching;
using Rabbit.Kernel.Environment;
using Rabbit.Kernel.Environment.Configuration;
using Rabbit.Kernel.Environment.Descriptor;
using Rabbit.Kernel.Environment.Descriptor.Models;
using Rabbit.Kernel.Environment.ShellBuilders;
using Rabbit.Kernel.Environment.State;
using Rabbit.Kernel.Extensions;
using Rabbit.Kernel.Logging;
using Rabbit.Kernel.Works;
using Rabbit.Web.Routes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rabbit.Web.Environment.Impl
{
    internal sealed class WebHost : IWebHost, IShellSettingsManagerEventHandler, IShellDescriptorManagerEventHandler
    {
        #region Field

        private readonly IExtensionLoaderCoordinator _extensionLoaderCoordinator;
        private readonly ICacheManager _cacheManager;
        private readonly IHostLocalRestart _hostLocalRestart;
        private readonly IExtensionMonitoringCoordinator _extensionMonitoringCoordinator;
        private readonly IShellSettingsManager _shellSettingsManager;
        private readonly IShellContextFactory _shellContextFactory;
        private readonly IRunningShellTable _runningShellTable;

        private readonly static object SyncLock = new object();

        private IEnumerable<ShellContext> _shellContexts;

        private readonly ContextState<IList<ShellSettings>> _tenantsToRestart;

        #endregion Field

        #region Property

        public ILogger Logger { get; set; }

        #endregion Property

        #region Constructor

        public WebHost(IExtensionLoaderCoordinator extensionLoaderCoordinator, ICacheManager cacheManager, IHostLocalRestart hostLocalRestart, IExtensionMonitoringCoordinator extensionMonitoringCoordinator, IShellSettingsManager shellSettingsManager, IShellContextFactory shellContextFactory, IRunningShellTable runningShellTable)
        {
            _extensionLoaderCoordinator = extensionLoaderCoordinator;
            _cacheManager = cacheManager;
            _hostLocalRestart = hostLocalRestart;
            _extensionMonitoringCoordinator = extensionMonitoringCoordinator;
            _shellSettingsManager = shellSettingsManager;
            _shellContextFactory = shellContextFactory;
            _runningShellTable = runningShellTable;

            _tenantsToRestart = new ContextState<IList<ShellSettings>>("DefaultHost.TenantsToRestart", () => new List<ShellSettings>());

            Logger = NullLogger.Instance;
        }

        #endregion Constructor

        #region Implementation of IHost

        /// <summary>
        /// 初始化。
        /// </summary>
        void IHost.Initialize()
        {
            Logger.Information("准备初始化主机。");
            BuildCurrent();
            Logger.Information("初始化主机完成。");
        }

        /// <summary>
        /// 重新加载扩展。
        /// </summary>
        void IHost.ReloadExtensions()
        {
            DisposeShellContext();
        }

        /// <summary>
        /// 获取一个外壳上下文。
        /// </summary>
        /// <param name="shellSettings">外壳设置。</param>
        /// <returns>外壳上下文。</returns>
        public ShellContext GetShellContext(ShellSettings shellSettings)
        {
            return BuildCurrent().SingleOrDefault(shellContext => shellContext.Settings.Name.Equals(shellSettings.Name));
        }

        #endregion Implementation of IHost

        #region Implementation of IShellSettingsManagerEventHandler

        /// <summary>
        /// 外壳设置保存成功之后。
        /// </summary>
        /// <param name="settings">外壳设置信息。</param>
        void IShellSettingsManagerEventHandler.Saved(ShellSettings settings)
        {
            Logger.Debug("外壳 {0} 被保存 ", settings.Name);

            if (settings.State == TenantState.Invalid)
                return;
            if (_tenantsToRestart.GetState().Any(t => t.Name.Equals(settings.Name)))
                return;
            Logger.Debug("标识租户: {0} {1} 需要重启", settings.Name, settings.State);
            _tenantsToRestart.GetState().Add(settings);
        }

        #endregion Implementation of IShellSettingsManagerEventHandler

        #region Implementation of IShellDescriptorManagerEventHandler

        /// <summary>
        /// 当外壳描述符发生变更时执行。
        /// </summary>
        /// <param name="descriptor">新的外壳描述符。</param>
        /// <param name="tenant">租户名称。</param>
        void IShellDescriptorManagerEventHandler.Changed(ShellDescriptor descriptor, string tenant)
        {
            if (_shellContexts == null)
                return;

            Logger.Debug("外壳发生了变化: " + tenant);

            var context = _shellContexts.FirstOrDefault(x => x.Settings.Name == tenant);

            if (context == null)
                return;

            //如果租户没有在运行则跳过
            if (context.Settings.State != TenantState.Running)
                return;

            //如果租户已标识为需要重启则跳过。
            if (_tenantsToRestart.GetState().Any(x => x.Name == tenant))
                return;

            Logger.Debug("标识租户: {0} 需要重启", tenant);
            _tenantsToRestart.GetState().Add(context.Settings);
        }

        #endregion Implementation of IShellDescriptorManagerEventHandler

        #region Implementation of IWebHost

        /// <summary>
        /// 请求开始时执行。
        /// </summary>
        void IWebHost.BeginRequest()
        {
            Logger.Debug("BeginRequest");
            BeginRequest();
        }

        /// <summary>
        /// 请求结束时候执行。
        /// </summary>
        void IWebHost.EndRequest()
        {
            Logger.Debug("EndRequest");
            EndRequest();
        }

        /// <summary>
        /// 创建一个独立的环境。
        /// </summary>
        /// <param name="shellSettings">外壳设置。</param>
        /// <returns>工作上下文范围。</returns>
        IWorkContextScope IHost.CreateStandaloneEnvironment(ShellSettings shellSettings)
        {
            Logger.Debug("为租户 {0} 创建独立的环境。", shellSettings.Name);

            MonitorExtensions();
            BuildCurrent();
            var shellContext = CreateShellContext(shellSettings);
            return shellContext.Container.CreateWorkContextScope();
        }

        #endregion Implementation of IWebHost

        #region Private Method

        private IEnumerable<ShellContext> BuildCurrent()
        {
            if (_shellContexts != null)
                return _shellContexts;
            lock (SyncLock)
            {
                if (_shellContexts != null)
                    return _shellContexts;
                SetupExtensions();
                MonitorExtensions();
                CreateAndActivateShells();
            }

            return _shellContexts;
        }

        private void SetupExtensions()
        {
            _extensionLoaderCoordinator.SetupExtensions();
        }

        private void MonitorExtensions()
        {
            _cacheManager.Get("RabbitHost_Extensions",
                              ctx =>
                              {
                                  _extensionMonitoringCoordinator.MonitorExtensions(ctx.Monitor);
                                  _hostLocalRestart.Monitor(ctx.Monitor);
                                  DisposeShellContext();
                                  return string.Empty;
                              });
        }

        private void DisposeShellContext()
        {
            Logger.Information("释放正在活动的外壳上下文。");

            if (_shellContexts == null)
                return;
            lock (SyncLock)
            {
                if (_shellContexts != null)
                {
                    foreach (var shellContext in _shellContexts)
                    {
                        shellContext.Shell.Terminate();
                        shellContext.Container.Dispose();
                    }
                }
            }
            _shellContexts = null;
        }

        private void CreateAndActivateShells()
        {
            Logger.Information("开始创建外壳上下文。");

            var allSettings = _shellSettingsManager
                                    .LoadSettings()
                                    .Where(settings => settings.State == TenantState.Running || settings.State == TenantState.Uninitialized)
                                    .ToArray();

            if (allSettings.Any())
            {
                Parallel.ForEach(allSettings, settings =>
                {
                    try
                    {
                        var context = CreateShellContext(settings);
                        ActivateShell(context);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, "一个租户无法被启动: " + settings.Name);
#if DEBUG
                        throw;
#endif
                    }
                });
            }

            Logger.Information("完成外壳创建");
        }

        private ShellContext CreateShellContext(ShellSettings settings)
        {
            Logger.Debug("为租户 {0} 创建外壳上下文", settings.Name);
            return _shellContextFactory.CreateShellContext(settings);
        }

        public void ActivateShell(ShellSettings settings)
        {
            Logger.Debug("准备激活外壳: " + settings.Name);

            //寻找相关的外壳上下文
            var shellContext = _shellContexts.FirstOrDefault(c => c.Settings.Name == settings.Name);

            if (shellContext == null && settings.State == TenantState.Disabled)
                return;

            if (shellContext == null || settings.State == TenantState.Uninitialized)
            {
                //创建外壳
                var context = CreateShellContext(settings);

                //激活外壳
                ActivateShell(context);
            }
            //租户被禁用则终止外壳
            else if (settings.State == TenantState.Disabled)
            {
                shellContext.Shell.Terminate();
                shellContext.Container.Dispose();
                _runningShellTable.Remove(settings);

                _shellContexts = _shellContexts.Where(shell => shell.Settings.Name != settings.Name);
            }
            //重新加载因为外壳设置被变更的外壳
            else
            {
                //释放之前的外壳上下文
                shellContext.Shell.Terminate();
                shellContext.Container.Dispose();

                var context = _shellContextFactory.CreateShellContext(settings);

                //激活并注册已修改的外壳上下文
                _shellContexts = _shellContexts.Where(shell => shell.Settings.Name != settings.Name).Union(new[] { context });

                context.Shell.Activate();
                _runningShellTable.Update(settings);
            }
        }

        private void ActivateShell(ShellContext context)
        {
            Logger.Debug("准备激活租户 {0} 的外壳上下文", context.Settings.Name);
            context.Shell.Activate();

            lock (this)
            {
                _shellContexts = (_shellContexts ?? Enumerable.Empty<ShellContext>())
                                .Where(c => c.Settings.Name != context.Settings.Name)
                                .Concat(new[] { context })
                                .ToArray();
            }

            _runningShellTable.Add(context.Settings);
        }

        private void BeginRequest()
        {
            MonitorExtensions();
            BuildCurrent();
            StartUpdatedShells();
        }

        private void EndRequest()
        {
            StartUpdatedShells();
        }

        private void StartUpdatedShells()
        {
            while (_tenantsToRestart.GetState().Any())
            {
                var settings = _tenantsToRestart.GetState().First();
                _tenantsToRestart.GetState().Remove(settings);
                Logger.Debug("更新外壳: " + settings.Name);
                lock (SyncLock)
                {
                    ActivateShell(settings);
                }
            }
        }

        #endregion Private Method
    }
}