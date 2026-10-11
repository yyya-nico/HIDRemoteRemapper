using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program {
    [STAThread] static void Main(string[] args) {
        if (args.Length > 0 && args[0] == "--self-test") {
            try {Remapper.Test();}
            catch(Exception ex) {File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test.txt"),"FAIL: "+ex);Environment.ExitCode=1;}
            return;
        }
        bool fileLogging=Array.Exists(args,a=>a.Equals("--log",StringComparison.OrdinalIgnoreCase));
        bool fresh; using (var mutex = new Mutex(true, "Local\\HIDRemoteRemapper", out fresh)) {
            if (!fresh) { MessageBox.Show("既に起動しています。"); return; }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            try {Application.EnableVisualStyles(); Application.Run(new Remapper(fileLogging));}
            catch(Exception ex) {if(fileLogging) {try {File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"crash.log"),ex.ToString());}catch(IOException){}catch(UnauthorizedAccessException){}} MessageBox.Show(ex.Message,"起動エラー");}
        }
    }
}
class Remapper : Form {
    readonly TextBox log = new TextBox { Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Both, Dock=DockStyle.Fill };
    readonly CheckBox enabled = new CheckBox { Text="Consumer変換 ON", Checked=true, AutoSize=true };
    readonly CheckBox keyboard = new CheckBox { Text="Fキー変換 ON（全キーボード対象）", Checked=true, AutoSize=true };
    readonly Dictionary<int,int> held = new Dictionary<int,int>();
    Dictionary<int,int> consumer = new Dictionary<int,int>(), keys = new Dictionary<int,int>();
    Dictionary<IntPtr,HashSet<int>> states = new Dictionary<IntPtr,HashSet<int>>();
    string vid, pid; int report;
    readonly string ini = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"remapper.ini");
    HookProc hookProc; IntPtr hook; NotifyIcon tray;
    readonly HashSet<IntPtr> seenDevices = new HashSet<IntPtr>();
    readonly HashSet<uint> registeredCollections = new HashSet<uint>();
    readonly string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"diagnostic.log");
    readonly bool fileLogging;
    readonly ConsumerRepeater repeats;
    readonly System.Windows.Forms.Timer repeatTimer = new System.Windows.Forms.Timer {Interval=15};
    readonly Stopwatch repeatClock = Stopwatch.StartNew();
    bool startHidden=true;
    bool restoringWindow;
    const uint WM_INPUT=0xFF;
    public Remapper(bool fileLogging, bool installHook=true) {
        this.fileLogging=fileLogging;
        repeats=new ConsumerRepeater(Send,()=>repeatClock.ElapsedMilliseconds);
        Text="HID Remote Remapper"; Width=900; Height=530;
        var bar=new FlowLayoutPanel { Dock=DockStyle.Top, AutoSize=true };
        bar.Controls.Add(enabled); bar.Controls.Add(keyboard);
        var reload=new Button { Text="INI再読込", AutoSize=true }; reload.Click+=(s,e)=>LoadIni(); bar.Controls.Add(reload);
        var edit=new Button { Text="INIを開く", AutoSize=true }; edit.Click+=(s,e)=>Process.Start("notepad.exe", "\""+ini+"\""); bar.Controls.Add(edit);
        var scan=new Button { Text="デバイス再検出", AutoSize=true }; scan.Click+=(s,e)=>ScanDevices(); bar.Controls.Add(scan);
        Controls.Add(log); Controls.Add(bar);
        enabled.CheckedChanged+=(s,e)=>ReleaseConsumer(); keyboard.CheckedChanged+=(s,e)=>ReleaseKeyboard();
        LoadIni(); hookProc=KeyboardHook;
        if(installHook) {
            hook=SetWindowsHookEx(13,hookProc,GetModuleHandle(null),0);
            if(hook==IntPtr.Zero) Write("キーボードフックの登録失敗: "+Marshal.GetLastWin32Error());
        }
        var menu=new ContextMenuStrip(); menu.Items.Add("表示",null,(s,e)=>RestoreWindow()); menu.Items.Add("終了",null,(s,e)=>Close());
        tray=new NotifyIcon { Icon=System.Drawing.SystemIcons.Application, Text=Text, Visible=true, ContextMenuStrip=menu }; tray.DoubleClick+=(s,e)=>RestoreWindow();
        WindowState=FormWindowState.Minimized; ShowInTaskbar=false;
        Resize+=(s,e)=>{if(!restoringWindow && WindowState==FormWindowState.Minimized) Hide();};
        Shown+=(s,e)=>{ScanDevices();if(startHidden)BeginInvoke(new Action(()=>{if(startHidden)Hide();}));};
        repeatTimer.Tick+=(s,e)=>repeats.Tick(); repeatTimer.Start();
    }
    void RestoreWindow() {
        // Cancel the initial hide before Show/handle recreation can raise Shown.
        startHidden=false;
        restoringWindow=true;
        try {ShowInTaskbar=true;Show();WindowState=FormWindowState.Normal;BringToFront();Activate();}
        finally {restoringWindow=false;}
    }
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        registeredCollections.Clear();
        var devices=new [] {new RAWINPUTDEVICE {page=12,usage=1,flags=0x2100,target=Handle},new RAWINPUTDEVICE {page=1,usage=6,flags=0x2100,target=Handle}};
        if(!RegisterRawInputDevices(devices,(uint)devices.Length,(uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)))) Write("Raw Input登録失敗: "+Marshal.GetLastWin32Error());
        else Write("Raw Input登録成功: Consumer 000C:0001 / Keyboard 0001:0006");
    }
    void Write(string text) { string line=DateTime.Now.ToString("HH:mm:ss.fff ")+text+Environment.NewLine; if(log.TextLength>80000) log.Clear(); log.AppendText(line); if(fileLogging) {try {File.AppendAllText(logPath,line);}catch(IOException){}catch(UnauthorizedAccessException){}} }
    static int Hex(string text) {return Convert.ToInt32(text.Trim().Replace("0x",""),16);}
    static int Key(string value) {
        Keys key; if(!Enum.TryParse<Keys>(value,true,out key) || key==Keys.None || (int)key>255) throw new Exception("無効なキー: "+value);
        return (int)key;
    }
    void LoadIni() {
        try {
            var c=new Dictionary<int,int>(); var k=new Dictionary<int,int>(); string section="", v="06E7", p="8099"; int r=2, delay=500, interval=50;
            foreach(var original in File.ReadAllLines(ini)) {
                string line=original.Trim(); if(line.Length==0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if(line.StartsWith("[") && line.EndsWith("]")) {section=line.Substring(1,line.Length-2).ToUpperInvariant();continue;}
                int eq=line.IndexOf('='); if(eq<1) throw new Exception("INIの形式: "+line);
                string a=line.Substring(0,eq).Trim(), b=line.Substring(eq+1).Trim();
                if(section=="CONSUMER") c.Add(Hex(a),Key(b));
                else if(section=="KEYBOARD") k.Add(Key(a),Key(b));
                else if(section=="REPEAT") {if(a.Equals("DelayMs",StringComparison.OrdinalIgnoreCase))delay=int.Parse(b);else if(a.Equals("IntervalMs",StringComparison.OrdinalIgnoreCase))interval=int.Parse(b);else throw new Exception("不明なRepeat設定: "+a);}
                else if(section=="DEVICE") {if(a.Equals("VendorId",StringComparison.OrdinalIgnoreCase)) v=Hex(b).ToString("X4"); else if(a.Equals("ProductId",StringComparison.OrdinalIgnoreCase)) p=Hex(b).ToString("X4"); else if(a.Equals("ReportId",StringComparison.OrdinalIgnoreCase)) r=Hex(b);}
                else throw new Exception("不明なセクション: "+section);
            }
            if(r>255) throw new Exception("ReportIdは00〜FF");
            if(delay<0 || delay>10000 || interval<15 || interval>10000)throw new Exception("Repeat: DelayMs=0〜10000、IntervalMs=15〜10000");
            ReleaseConsumer(); ReleaseKeyboard(); repeats.DelayMs=delay;repeats.IntervalMs=interval;consumer=c; keys=k; vid=v; pid=p; report=r; Write("INI読込成功 / VID="+vid+" PID="+pid);
        } catch(Exception ex) {Write("設定エラー（既存設定を保持）: "+ex.Message);}
    }
    protected override void WndProc(ref Message m) {
        if(m.Msg==WM_INPUT) {try {ReadRaw(m.LParam);} catch(Exception ex) {Write("入力エラー: "+ex.Message);}}
        if(m.Msg==0xFE) {if(m.WParam.ToInt32()==2) ReleaseConsumer(); BeginInvoke(new Action(ScanDevices));}
        base.WndProc(ref m);
    }
    string DeviceName(IntPtr device) {
        uint len=0; GetRawInputDeviceInfo(device,0x20000007,IntPtr.Zero,ref len); if(len==0 || len>32768) return "";
        IntPtr ptr=Marshal.AllocHGlobal((int)(len+1)*2); try {if(GetRawInputDeviceInfo(device,0x20000007,ptr,ref len)==uint.MaxValue)return ""; return Marshal.PtrToStringUni(ptr).ToUpperInvariant();} finally {Marshal.FreeHGlobal(ptr);}
    }
    static bool MatchesName(string name,string vendor,string product) {
        return (name.Contains("VID_"+vendor) || name.Contains("VID&"+vendor) || name.Contains("VID&01"+vendor) || name.Contains("VID&02"+vendor) || name.Contains("VID&0001"+vendor) || name.Contains("VID&0002"+vendor)) && (name.Contains("PID_"+product) || name.Contains("PID&"+product));
    }
    uint[] DeviceInfo(IntPtr device) {
        uint size=32; IntPtr p=Marshal.AllocHGlobal(32); try {
            for(int i=0;i<32;i+=4)Marshal.WriteInt32(p,i,0); Marshal.WriteInt32(p,32);
            if(GetRawInputDeviceInfo(device,0x2000000B,p,ref size)==uint.MaxValue) {
                int error=Marshal.GetLastWin32Error(); throw new System.ComponentModel.Win32Exception(error,"RIDI_DEVICEINFO失敗: "+error);
            }
            var values=new uint[8];for(int i=0;i<8;i++)values[i]=(uint)Marshal.ReadInt32(p,i*4);return values;
        } finally {Marshal.FreeHGlobal(p);}
    }
    bool IsTarget(string name,uint[] info) {
        return (info[1]==2 && info[2]==Hex(vid) && info[3]==Hex(pid)) || MatchesName(name,vid,pid);
    }
    void ScanDevices() {
        try {
            seenDevices.Clear(); uint count=0; uint stride=(uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            if(GetRawInputDeviceList(IntPtr.Zero,ref count,stride)==uint.MaxValue)throw new Exception("デバイス列挙失敗");
            IntPtr list=Marshal.AllocHGlobal(checked((int)(count*stride))); try {
                uint result=GetRawInputDeviceList(list,ref count,stride);if(result==uint.MaxValue)throw new Exception("デバイス列挙失敗");
                int targets=0;var registrations=new List<RAWINPUTDEVICE>();
                for(int i=0;i<result;i++) {
                    IntPtr device=Marshal.ReadIntPtr(list,i*(int)stride); string name=DeviceName(device);uint[] info=DeviceInfo(device);bool target=IsTarget(name,info);
                    Write("DEVICE "+(target?"[対象] ":"")+"type="+info[1]+(info[1]==2?" VID="+info[2].ToString("X4")+" PID="+info[3].ToString("X4")+" Usage="+(info[5]&65535).ToString("X4")+":"+(info[5]>>16).ToString("X4"):"")+" "+name);
                    if(target){targets++;if(info[1]==2 && registeredCollections.Add(info[5]))registrations.Add(new RAWINPUTDEVICE {page=(ushort)info[5],usage=(ushort)(info[5]>>16),flags=0x2100,target=Handle});}
                }
                foreach(var registration in registrations) {
                    if(!RegisterRawInputDevices(new[]{registration},1,(uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)))) {registeredCollections.Remove((uint)registration.page|((uint)registration.usage<<16));Write("対象Usage登録失敗: "+Marshal.GetLastWin32Error());}
                }
                Write("検出完了: 対象コレクション "+targets+"件。ボタンを押して確認してください。");
            }finally {Marshal.FreeHGlobal(list);}
        }catch(Exception ex){Write("検出エラー: "+ex.Message);}
    }
    void ReadRaw(IntPtr input) {
        uint size=0; uint header=(uint)(8+2*IntPtr.Size);
        if(GetRawInputData(input,0x10000003,IntPtr.Zero,ref size,header)==uint.MaxValue || size<header || size>1048576)return;
        IntPtr ptr=Marshal.AllocHGlobal((int)size); try {
            if(GetRawInputData(input,0x10000003,ptr,ref size,header)!=size)return;
            uint type=(uint)Marshal.ReadInt32(ptr); IntPtr device=Marshal.ReadIntPtr(ptr,8);
            // Synthetic/system input can have no physical device (including our SendInput).
            // It must not be looked up or treated as another remote button.
            if(device==IntPtr.Zero)return;
            string name=DeviceName(device); uint[] info;
            try {info=DeviceInfo(device);}
            catch(System.ComponentModel.Win32Exception ex) {
                if(ex.NativeErrorCode!=6)throw;
                if(seenDevices.Add(device))Write("受信元を確認できないためスキップ（切断済み等）: handle=0x"+device.ToInt64().ToString("X")+" "+name);
                return;
            }
            bool target=IsTarget(name,info);
            if(seenDevices.Add(device))Write("受信元 "+(target?"[対象] ":"[対象外] ")+"type="+type+" "+name);
            if(!target)return;
            int offset=(int)header;
            if(type==1) {int vk=(ushort)Marshal.ReadInt16(ptr,offset+6); int flags=(ushort)Marshal.ReadInt16(ptr,offset+2); Write("REMOTE Keyboard "+((Keys)vk)+" "+((flags&1)!=0?"UP":"DOWN"));return;}
            if(type!=2 || size<header+8)return;
            uint length=(uint)Marshal.ReadInt32(ptr,offset), count=(uint)Marshal.ReadInt32(ptr,offset+4);
            if(length==0 || (ulong)length*count>size-header-8)return;
            for(uint i=0;i<count;i++) {
                byte[] data=new byte[length]; Marshal.Copy(IntPtr.Add(ptr,offset+8+(int)(length*i)),data,0,(int)length);
                Write("REMOTE HID "+BitConverter.ToString(data).Replace('-',' '));
                if(data.Length!=9 || data[0]!=report)continue;
                var next=Decode(data); HashSet<int> old; if(!states.TryGetValue(device,out old))old=new HashSet<int>();
                states[device]=next;
                if(!enabled.Checked)continue;
                foreach(int usage in old) if(!next.Contains(usage) && consumer.ContainsKey(usage)) ConsumerKey(consumer[usage],false);
                foreach(int usage in next) if(!old.Contains(usage) && consumer.ContainsKey(usage)) {Write("Usage "+usage.ToString("X4")+" → "+(Keys)consumer[usage]);ConsumerKey(consumer[usage],true);}
            }
        } finally {Marshal.FreeHGlobal(ptr);}
    }
    void ConsumerKey(int key,bool down) {repeats.Change(key,down);}
    void ReleaseConsumer() {repeats.Clear();states.Clear();}
    void ReleaseKeyboard() {foreach(int key in held.Values)Send(key,false);held.Clear();}
    IntPtr KeyboardHook(int code,IntPtr w,IntPtr l) {
        if(code>=0) {
            int flags=Marshal.ReadInt32(l,8), vk=Marshal.ReadInt32(l); int msg=w.ToInt32(); bool down=msg==0x100 || msg==0x104, up=msg==0x101 || msg==0x105;
            if((flags&0x10)==0 && (down||up) && keyboard.Checked && keys.ContainsKey(vk)) {
                if(down) {held[vk]=keys[vk];Send(keys[vk],true);} else {int output;if(held.TryGetValue(vk,out output)){Send(output,false);held.Remove(vk);}}
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(hook,code,w,l);
    }
    void Send(int key,bool down) {
        uint flags=down?0u:2u; if(key>=0x21 && key<=0x2E) flags|=1;
        var inputs=new [] {new INPUT {type=1,data=new INPUTUNION {ki=new KEYBDINPUT {vk=(ushort)key,flags=flags}}}};
        if(SendInput(1,inputs,Marshal.SizeOf(typeof(INPUT)))!=1)Write("SendInput失敗: "+Marshal.GetLastWin32Error()+"（対象アプリの権限も確認）");
    }
    protected override void OnFormClosed(FormClosedEventArgs e) {repeatTimer.Stop();repeatTimer.Dispose();ReleaseConsumer();ReleaseKeyboard();if(hook!=IntPtr.Zero)UnhookWindowsHookEx(hook);tray.Visible=false;tray.Dispose();base.OnFormClosed(e);}
    static HashSet<int> Decode(byte[] data) {var set=new HashSet<int>();for(int j=1;j+1<data.Length;j+=2){int u=data[j]|data[j+1]<<8;if(u!=0)set.Add(u);}return set;}
    public static void Test() {
        var a=Decode(new byte[]{2,0x30,0,0x23,2,0,0,0,0});if(!a.SetEquals(new[]{0x30,0x223}))throw new Exception("decode");
        if(Decode(new byte[9]).Count!=0 || Key("F1")!=112 || Key("Escape")!=27)throw new Exception("keys");
        if(Marshal.SizeOf(typeof(INPUT))!=40 || Marshal.SizeOf(typeof(RAWINPUTDEVICE))!=16)throw new Exception("ABI");
        if(!MatchesName("HID#VID_06E7&PID_8099","06E7","8099") || !MatchesName("HID#VID&000206E7_PID&8099","06E7","8099") || MatchesName("HID#VID_1234&PID_8099","06E7","8099"))throw new Exception("device match");
        if(!MatchesName("HID#DEV_VID&0206E7_PID&8099","06E7","8099"))throw new Exception("BLE match");
        long now=0;var events=new List<string>();var repeat=new ConsumerRepeater((key,down)=>events.Add(key+":"+down),()=>now);
        repeat.Change(39,true);now=499;repeat.Tick();if(events.Count!=1)throw new Exception("repeat delay");
        now=500;repeat.Tick();now=549;repeat.Tick();if(events.Count!=2)throw new Exception("repeat interval");
        now=550;repeat.Tick();repeat.Change(39,false);now=2000;repeat.Tick();
        if(string.Join(",",events)!="39:True,39:True,39:True,39:False")throw new Exception("repeat release");
        events.Clear();repeat.Change(40,true);repeat.Change(40,true);repeat.Change(40,false);now=2500;repeat.Tick();repeat.Clear();now=5000;repeat.Tick();
        if(string.Join(",",events)!="40:True,40:True,40:False")throw new Exception("repeat shared key / clear");
        TestWindowLifecycle();
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test.txt"),"PASS: report decoding, release report, key names, x64 Win32 ABI, USB/Bluetooth device matching, repeat delay/interval/release/shared-key/clear, minimized startup / tray restore / minimize / restore");
    }
    static void TestWindowLifecycle() {
        Exception failure=null;int stage=0;
        using(var form=new Remapper(false,false))using(var timer=new System.Windows.Forms.Timer {Interval=150}) {
            timer.Tick+=(s,e)=>{
                try {
                    switch(stage++) {
                    case 0:
                        if(form.Visible || !form.keyboard.Checked)throw new Exception("minimized startup / keyboard default");
                        form.tray.ContextMenuStrip.Items[0].PerformClick();break;
                    case 1:
                        if(!form.Visible || form.WindowState!=FormWindowState.Normal || !form.ShowInTaskbar)throw new Exception("tray restore");
                        form.WindowState=FormWindowState.Minimized;break;
                    case 2:
                        if(form.Visible)throw new Exception("minimize hide");
                        form.tray.ContextMenuStrip.Items[0].PerformClick();break;
                    case 3:
                        if(!form.Visible || form.WindowState!=FormWindowState.Normal)throw new Exception("second tray restore");
                        timer.Stop();form.Close();break;
                    }
                }catch(Exception ex){failure=ex;timer.Stop();form.Close();}
            };
            timer.Start();Application.Run(form);
        }
        if(failure!=null)throw failure;
    }
    delegate IntPtr HookProc(int code,IntPtr w,IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE {public ushort page,usage;public uint flags;public IntPtr target;}
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICELIST {public IntPtr device;public uint type;}
    [StructLayout(LayoutKind.Sequential)] struct INPUT {public uint type;public INPUTUNION data;}
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION {[FieldOffset(0)]public KEYBDINPUT ki;[FieldOffset(0)]public MOUSEINPUT mi;}
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT {public ushort vk,scan;public uint flags,time;public UIntPtr extra;}
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT {public int x,y;public uint data,flags,time;public UIntPtr extra;}
    [DllImport("user32.dll",SetLastError=true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d,uint n,uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h,uint command,IntPtr data,ref uint size,uint header);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetRawInputDeviceInfo(IntPtr h,uint command,IntPtr data,ref uint size);
    [DllImport("user32.dll",SetLastError=true)] static extern uint GetRawInputDeviceList(IntPtr list,ref uint count,uint stride);
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,INPUT[] data,int size);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int id,HookProc proc,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h,int code,IntPtr w,IntPtr l);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
}
class ConsumerRepeater {
    class State {public int Count;public long Next;}
    readonly Dictionary<int,State> held=new Dictionary<int,State>();
    readonly Action<int,bool> send;readonly Func<long> clock;
    public int DelayMs=500,IntervalMs=50;
    public ConsumerRepeater(Action<int,bool> send,Func<long> clock) {this.send=send;this.clock=clock;}
    public void Change(int key,bool down) {
        State state;held.TryGetValue(key,out state);
        if(down) {if(state==null){held[key]=new State {Count=1,Next=clock()+DelayMs};send(key,true);}else state.Count++;}
        else if(state!=null && --state.Count==0){held.Remove(key);send(key,false);}
    }
    public void Tick() {
        long now=clock();foreach(var pair in held)if(now>=pair.Value.Next){pair.Value.Next=now+IntervalMs;send(pair.Key,true);}
    }
    public void Clear() {foreach(int key in held.Keys)send(key,false);held.Clear();}
}
