using System;
using System.Reflection;
using Game;
using Game.Tools;
using SkylineTogether.Core;
using SkylineTogether.Core.Session;
using SkylineTogether.Session;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// gameplay branch: the no-predict gate for viewers (ARCH §5.3, R10 §7.4). Runs in ToolUpdate right before
    /// <c>ToolOutputSystem</c>: when the local player is a viewer and the active tool chose <c>Apply</c>, it rewrites the
    /// tool's <c>applyMode</c> to <c>Clear</c> through its protected setter (<c>ToolBaseSystem.cs:182</c>), so the vanilla
    /// <c>ToolClearSystem</c> removes the preview exactly as on a mouse move and nothing is committed or proposed
    /// (<c>Apply → None</c> would leave the queued definitions in an undefined state). The host refuses a viewer's
    /// proposals anyway (no permission), so this gate is comfort, not security.
    /// </summary>
    public sealed class ViewerGateSystem : GameSystemBase
    {
        private ToolSystem _tools;
        private MethodInfo _setApplyMode;
        private bool _failed;
        private double _lastToast = double.NegativeInfinity;

        protected override void OnCreate()
        {
            base.OnCreate();
            _tools = World.GetOrCreateSystemManaged<ToolSystem>();
            _setApplyMode = typeof(ToolBaseSystem).GetProperty("applyMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetSetMethod(true);
            if (_setApplyMode == null) Mod.Log?.Warn(LogCategory.Gate, "ToolBaseSystem.applyMode setter not found: viewers' tool applies are refused by the host instead");
        }

        protected override void OnUpdate()
        {
            if (_failed || _setApplyMode == null) return;
            try
            {
                SessionManager sessions = Mod.Sessions;
                SessionClient client = sessions?.Client;
                if (client == null || client.State != ClientState.Live || RolePermissions.Predicts(client.LocalRole)) return;
                ToolBaseSystem tool = _tools.activeTool;
                if (tool == null || tool.applyMode != Game.Tools.ApplyMode.Apply) return;
                _setApplyMode.Invoke(tool, new object[] { Game.Tools.ApplyMode.Clear });
                double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
                if (now - _lastToast > 5.0)
                {
                    _lastToast = now;
                    SessionManager.Notify(Settings.LocaleSource.Resolve("st.toast.viewerEdit"));
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                Mod.Log?.Error(LogCategory.Gate, "viewer gate failed; disabled (the host still refuses viewers' edits)", ex);
            }
        }
    }
}
