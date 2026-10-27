using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Alife.Framework;
using Microsoft.Extensions.Logging;
namespace Marisa.QuickChat;
public sealed class AlifeQuickChatWindowFactory(IAlifeApplication app) : IQuickChatWindowFactory {
    public IQuickChatWindow Create(ILogger<QuickChatModule> logger) => new AlifeQuickChatWindow(app, logger);
}
public sealed class WindowsQuickChatShortcutService : IQuickChatShortcutService {
    [DllImport("user32.dll", SetLastError=true)] static extern bool RegisterHotKey(IntPtr hWnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd,int id);
    sealed class HotkeyWindow : Form {
        public readonly Dictionary<int, Action> callbacks = new();
        public HotkeyWindow() { ShowInTaskbar=false; FormBorderStyle=FormBorderStyle.FixedToolWindow; Opacity=0; }
        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);
        protected override void WndProc(ref Message m) { if(m.Msg==0x0312 && callbacks.TryGetValue(m.WParam.ToInt32(),out var cb)) { Task.Run(cb); } base.WndProc(ref m); }
    }
    readonly ILogger logger;
    readonly Dictionary<string,int> registered = new(StringComparer.OrdinalIgnoreCase);
    readonly TaskCompletionSource<HotkeyWindow> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int nextId;
    public WindowsQuickChatShortcutService(ILogger<QuickChatModule> logger) {
        this.logger=logger;
        var t=new Thread(() => { try { var form=new HotkeyWindow(); _=form.Handle; ready.TrySetResult(form); Application.Run(form); } catch(Exception ex) { ready.TrySetException(ex); } });
        t.IsBackground=true; t.SetApartmentState(ApartmentState.STA); t.Start();
    }
    public void Register(string accelerator,Action callback) {
        var parts=accelerator.Split('+',StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); uint mods=0; Keys key=Keys.None;
        foreach(var part in parts) { if(part.Equals("Alt",StringComparison.OrdinalIgnoreCase)) mods|=1; else if(part.Equals("Control",StringComparison.OrdinalIgnoreCase)||part.Equals("Ctrl",StringComparison.OrdinalIgnoreCase)) mods|=2; else if(part.Equals("Shift",StringComparison.OrdinalIgnoreCase)) mods|=4; else if(part.Equals("Super",StringComparison.OrdinalIgnoreCase)||part.Equals("Win",StringComparison.OrdinalIgnoreCase)) mods|=8; else if(!Enum.TryParse(part,true,out key)) throw new ArgumentException("不支持的快捷键: "+accelerator); }
        if(key==Keys.None) throw new ArgumentException("快捷键缺少主键: "+accelerator);
        var form=ready.Task.GetAwaiter().GetResult(); int id=Interlocked.Increment(ref nextId); bool ok=false;
        form.Invoke(() => { ok=RegisterHotKey(form.Handle,id,mods,(uint)key); if(ok) form.callbacks[id]=callback; });
        if(!ok) throw new InvalidOperationException("无法注册快捷键: "+accelerator);
        lock(registered) registered[accelerator]=id;
    }
    public void Unregister(string accelerator) { int id; lock(registered) { if(!registered.Remove(accelerator,out id)) return; } var form=ready.Task.GetAwaiter().GetResult(); form.Invoke(() => { UnregisterHotKey(form.Handle,id); form.callbacks.Remove(id); }); }
    public void UnregisterAll(IEnumerable<string> accelerators) { foreach(var a in accelerators) try { Unregister(a); } catch(Exception ex) { logger.LogWarning(ex,"注销快捷键失败"); } }
}
