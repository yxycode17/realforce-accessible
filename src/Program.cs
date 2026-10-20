using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using RFSWLib;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace RealforceAccessible {
static class Program {
    public static readonly string RuntimeDirectory = AppDomain.CurrentDomain.BaseDirectory;
    [STAThread] static int Main(string[] args) {
        try {
            foreach(string name in new[]{"RFSWLib.dll","R30B.r3k"})
                if(!File.Exists(Path.Combine(RuntimeDirectory,name)))
                    throw new FileNotFoundException("程序同目录缺少依赖文件："+name+"。请复制完整 dist 目录。");
            Assembly.LoadFrom(Path.Combine(RuntimeDirectory,"RFSWLib.dll"));
            return Run(args);
        } catch(Exception e) {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"last-error.txt"), e.ToString());
            if(args.Length == 0) MessageBox.Show(e.Message,"REALFORCE 无障碍助手");
            return 1;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Run(string[] args) {
        Names.Load();
        if(args.Length > 0 && args[0] == "--probe") {
            var devices = Backend.Discover();
            string output = "通信库："+typeof(KeyboardEntryR3).Assembly.Location+"\r\n找到设备：" + devices.Count + "\r\n";
            foreach(var d in devices) { var s = Backend.Read(d); output += s.Report() + "\r\n"; }
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"probe.txt"),output,Encoding.UTF8);
            return devices.Count == 0 ? 2 : 0;
        }
        if(args.Length > 0 && args[0] == "--self-test") { Checks.Run(); return 0; }
        if(args.Length > 0 && args[0] == "--configuration-test") {
            var ds=Backend.Discover();if(ds.Count!=1)throw new IOException("此检查要求连接一台支持的键盘。");
            var s=Backend.Read(ds[0]);var imported=ConfigurationFile.Decode(ConfigurationFile.Encode(s),s);
            if(imported.Any(p=>!p.Value.SequenceEqual(s.Edit[p.Key])))throw new IOException("配置往返不一致。");
            File.WriteAllText(Path.Combine(RuntimeDirectory,"configuration-test.txt"),"PASS: real device read and JSON roundtrip; "+s.Keys.Count+" keys; no device writes.");return 0;
        }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm()); return 0;
    }
}
sealed class Choice {
    public int Value; public string Text;
    public Choice(int value,string text) { Value=value; Text=text; }
    public override string ToString() { return Text; }
}
static class Names {
    public static SortedDictionary<int,string> Codes = new SortedDictionary<int,string>();
    public static readonly string[] Modifiers={"左 Ctrl","左 Shift","左 Alt","左 Win","右 Ctrl","右 Shift","右 Alt","右 Win"};
    public static readonly string[] Apc={"0.8 毫米","1.5 毫米","2.2 毫米","3.0 毫米"};
    public static readonly string[] Modes={"全部按键：0.8 毫米","全部按键：1.5 毫米","全部按键：2.2 毫米","全部按键：3.0 毫米","逐键配置 1","逐键配置 2"};
    public static readonly string[] Layers={"A 普通层","A Fn 层","B 普通层","B Fn 层"};
    public static void Load() {
        using(var r=new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("key-names.tsv"),Encoding.UTF8)) {
            string line; while((line=r.ReadLine())!=null) { var p=line.Split('\t'); int code; if(p.Length==2 && int.TryParse(p[0],out code) && !Codes.ContainsKey(code)) Codes.Add(code,p[1]); }
        }
        Codes[0]="未分配"; Codes[4096]="Fn";
        for(int i=0;i<16;i++) Codes[4320+i]="自定义快捷键 "+(i+1);
    }
    public static string Key(int value) { string n; return Codes.TryGetValue(value,out n)?n:"未知键码 0x"+value.ToString("X4"); }
    public static string Level(int value) { return value>=0 && value<4?Apc[value]:"自定义或未知档位（"+value+"）"; }
    public static string Mode(int value) { return value>=0 && value<Modes.Length?Modes[value]:"未知模式（"+value+"）"; }
    public static string Eco(int value) { string[] modes={"级别 1：指示灯关闭，闲置 10 分钟关机","级别 2：指示灯开启，闲置 10 分钟关机","级别 3：指示灯开启，闲置 30 分钟关机","自定义","关闭"}; return value>=0 && value<5?modes[value]:"未知（"+value+"）"; }
    public static string EcoAction(int value) { return value==0?"关机":value==1?"关闭无线连接":value==2?"保持开机":"未知（"+value+"）"; }
    public static string Shortcut(int mod,int key) {
        var parts=new List<string>(); for(int i=0;i<8;i++) if((mod & (1<<i))!=0) parts.Add(Modifiers[i]);
        if(key!=0) parts.Add(Key(key)); return parts.Count==0?"未设置":string.Join(" + ",parts);
    }
}
sealed class Device {
    public string Path,Model;
    public override string ToString() { return Model + (KeyboardEnumurator.isBleConnect(Path)?"（蓝牙）":"（USB）"); }
}
sealed class PhysicalKey {
    public int Index; public string Name;
    public override string ToString() { return Name; }
}
sealed class Snapshot {
    public Device Device; public string Firmware; public DateTime Time; public string Battery="未读取";
    public List<PhysicalKey> Keys = new List<PhysicalKey>();
    public Dictionary<int,byte[]> Original = new Dictionary<int,byte[]>();
    public Dictionary<int,byte[]> Edit = new Dictionary<int,byte[]>();
    public int ShortcutCount; public bool NeedsRefresh,CustomEco;
    public uint[] KeyCounts;
    public DateTime StatisticsTime;
    public byte[] Pairing;
    public string[] SlotNames=new string[4];
    public int LastSlot=-1;
    public string PairingText() {
        var lines=new List<string>{"读取时间："+Time.ToString("HH:mm:ss"),"当前连接："+(KeyboardEnumurator.isBleConnect(Device.Path)?"蓝牙":"USB"),"最近连接的蓝牙槽位："+(LastSlot>=0 && LastSlot<4?(LastSlot+1).ToString():"未获取有效槽位")};
        for(int i=0;i<4;i++)lines.Add("槽位 "+(i+1)+"："+(Pairing==null?"配对状态未获取":Pairing[i]==0?"未配对":"已配对")+(Pairing!=null && Pairing[i]==0?"":"；名称："+(SlotNames[i]==null?"未获取":SlotNames[i].Length==0?"未命名":SlotNames[i])));
        lines.Add("最近连接槽位不代表当前正通过该槽位连接。按 F5 更新。");return string.Join("\r\n",lines);
    }
    public static string DeviceName(byte[] data) { return data.All(b=>b==255)?"":Encoding.ASCII.GetString(data.TakeWhile(b=>b!=0).ToArray()); }
    public void SetName(string name) { if(name.Length>32 || name.Any(c=>c<32 || c>126))throw new ArgumentException("名称最多 32 个英文、数字或半角符号。"); var b=new byte[32];Encoding.ASCII.GetBytes(name).CopyTo(b,0);Edit[40]=b; }
    public static uint CountValue(byte[] data,int offset) { return ((uint)data[offset]<<24)|((uint)data[offset+1]<<16)|((uint)data[offset+2]<<8)|data[offset+3]; }
    public IEnumerable<PhysicalKey> RankedKeys() { return Keys.OrderByDescending(k=>KeyCounts[k.Index]).ThenBy(k=>k.Index); }
    public string StatisticsSummary() { return KeyCounts==null?"本次未获取按键统计。":"读取时间："+StatisticsTime.ToString("HH:mm:ss")+"；实体按键次数合计："+Keys.Aggregate(0UL,(sum,k)=>sum+KeyCounts[k.Index])+"。按次数降序排列，F5 更新。"; }
    public List<string> Notes = new List<string>();
    public int Byte(int block,int index) { return Edit[block+index/32][index%32]; }
    public void SetByte(int block,int index,int value) { Edit[block+index/32][index%32]=checked((byte)value); }
    public void SetCustomEco(int index,int value) {
        if(Byte(54,0)==0){SetByte(54,0,30);SetByte(54,1,1);SetByte(54,2,1);} SetByte(54,index,value);
    }
    public int Mapping(int layer,int key) { int b=64+layer*8; return (Byte(b,key*2)<<8)|Byte(b,key*2+1); }
    public void SetMapping(int layer,int key,int value) { SetByte(64+layer*8,key*2,value>>8); SetByte(64+layer*8,key*2+1,value&255); }
    public bool Dirty { get { return Original.Any(p=>!p.Value.SequenceEqual(Edit[p.Key])); } }
    public string Status() {
        return "型号："+Device.Model+"\r\n固件："+Firmware+"\r\n连接："+(KeyboardEnumurator.isBleConnect(Device.Path)?"蓝牙":"USB")+"\r\n电量："+Battery+"\r\n读取时间："+Time.ToString("HH:mm:ss")+
          "\r\n键盘名称："+DeviceName(Original[40])+
          "\r\n当前 APC 模式（上次读取）："+Names.Mode(Original[48][0])+"\r\n当前 ECO（上次读取）："+Names.Eco(Original[52][0])+
          "\r\n当前映射（上次读取）："+(Original[49][0]==0?"A":Original[49][0]==1?"B":"未知")+
          (CustomEco?"\r\nECO 自定义配置："+EcoDetails(Original[54]):"")+
          "\r\n快捷键槽位："+ShortcutCount+"\r\n"+(Dirty?"助手中存在尚未保存的编辑。":"助手中没有待保存的编辑。")+
          "\r\n按 F5 读取键盘当前配置，包括键盘上尚未保存的调整。读取结果不能判断是否已永久保存；未保存的调整在重新拔插后会还原。"+(NeedsRefresh?"\r\n上次保存失败；这些数据可能已过期，必须刷新后再保存。":"")+
          (Notes.Count>0?"\r\n"+string.Join("\r\n",Notes):"");
    }
    public static string EcoDetails(byte[] b) {
        int duration=b[0]==0?30:b[0],led=b[0]==0?1:b[1],action=b[0]==0?1:b[2];
        return "指示灯"+(led==1?"开启":"关闭")+"；"+(action==2?"保持开机":"闲置 "+duration+" 分钟后"+Names.EcoAction(action));
    }
    public string KeyDetails(PhysicalKey k) {
        var b=new StringBuilder(k.Name+"\r\n"+(Dirty?"以下为当前编辑值，待保存修改尚未写入键盘。\r\n":""));
        for(int i=0;i<4;i++) { int v=Mapping(i,k.Index); b.AppendLine(Names.Layers[i]+"："+Names.Key(v));
            if(v>=4320 && v<4320+ShortcutCount) b.AppendLine("组合："+Names.Shortcut(Byte(45,(v-4320)*2),Byte(45,(v-4320)*2+1))); }
        b.AppendLine("逐键 APC 配置 1："+Names.Level(Byte(56,k.Index)));
        b.AppendLine("逐键 APC 配置 2："+Names.Level(Byte(60,k.Index)));
        int mode=Byte(48,0);
        b.AppendLine((Dirty?"按当前编辑配置计算的 APC：":"按上次读取的当前配置计算的 APC：")+(mode<4?Names.Level(mode):mode==4?Names.Level(Byte(56,k.Index)):mode==5?Names.Level(Byte(60,k.Index)):"未知"));
        return b.ToString();
    }
    public List<string> Changes() {
        var lines=new List<string>();
        if(Original.ContainsKey(40) && !Original[40].SequenceEqual(Edit[40]))lines.Add("键盘名称："+DeviceName(Original[40])+" → "+DeviceName(Edit[40]));
        foreach(var k in Keys) {
            for(int i=0;i<4;i++) { int offset=k.Index*2,block=64+i*8+offset/32; int old=(Original[block][offset%32]<<8)|Original[block][offset%32+1]; int value=Mapping(i,k.Index);
                if(old!=value) lines.Add(k.Name+" / "+Names.Layers[i]+"："+Names.Key(old)+" → "+Names.Key(value)); }
            for(int i=0;i<2;i++) { int block=56+i*4+k.Index/32; int old=Original[block][k.Index%32],value=Byte(56+i*4,k.Index);
                if(old!=value) lines.Add(k.Name+" / APC 配置 "+(i+1)+"："+Names.Level(old)+" → "+Names.Level(value)); }
        }
        for(int i=0;i<ShortcutCount;i++) if(Original[45][i*2]!=Edit[45][i*2] || Original[45][i*2+1]!=Edit[45][i*2+1])
            lines.Add("快捷键 "+(i+1)+"："+Names.Shortcut(Original[45][i*2],Original[45][i*2+1])+" → "+Names.Shortcut(Byte(45,i*2),Byte(45,i*2+1)));
        if(Original[48][0]!=Edit[48][0]) lines.Add("APC 模式："+Names.Mode(Original[48][0])+" → "+Names.Mode(Edit[48][0]));
        if(Original[49][0]!=Edit[49][0]) lines.Add("当前键盘方案："+(Original[49][0]==0?"A":"B")+" → "+(Edit[49][0]==0?"A":"B"));
        if(Original[52][0]!=Edit[52][0]) lines.Add("ECO："+Names.Eco(Original[52][0])+" → "+Names.Eco(Edit[52][0]));
        if(CustomEco && !Original[54].SequenceEqual(Edit[54])) lines.Add("ECO 自定义："+EcoDetails(Original[54])+" → "+EcoDetails(Edit[54]));
        return lines.Select(line=>{
            int colon=line.IndexOf('：'),arrow=line.IndexOf(" → ",StringComparison.Ordinal);
            return colon<0 || arrow<0?line:line.Substring(0,colon)+"\r\n  当前值（上次读取）："+line.Substring(colon+1,arrow-colon-1)+"\r\n  目标值："+line.Substring(arrow+3);
        }).ToList();
    }
    public string Report() { return Status()+"\r\n\r\n配对槽位：\r\n"+PairingText()+"\r\n\r\n按键统计："+StatisticsSummary()+"\r\n"+(KeyCounts==null?"":string.Join("\r\n",RankedKeys().Select(k=>k.Name+"："+KeyCounts[k.Index]+" 次")))+"\r\n\r\n"+string.Join("\r\n",Keys.Select(KeyDetails))+"\r\n"+
        string.Join("\r\n",Enumerable.Range(0,ShortcutCount).Select(i=>"快捷键 "+(i+1)+"："+Names.Shortcut(Byte(45,i*2),Byte(45,i*2+1)))); }
}
static class Backend {
    const string SupportedLibrary="291fc248d9a9cf0b5e9275b9e4c8dfe2e5c59f60e09e1486b0c6e63e4c642a3e";
    static void CheckLibrary() {
        if(!string.Equals(Path.GetFullPath(typeof(KeyboardEntryR3).Assembly.Location),Path.GetFullPath(Path.Combine(Program.RuntimeDirectory,"RFSWLib.dll")),StringComparison.OrdinalIgnoreCase))
            throw new IOException("通信库必须从程序同目录加载。");
        using(var hash=SHA256.Create()) using(var f=File.OpenRead(Path.Combine(Program.RuntimeDirectory,"RFSWLib.dll")))
            if(BitConverter.ToString(hash.ComputeHash(f)).Replace("-","").ToLowerInvariant()!=SupportedLibrary)
                throw new IOException("REALFORCE 库版本与已分析版本不同，已停止连接。请更新适配后重试。");
    }
    public static void Check(LibParam.AppError e,string op) { if(e!=LibParam.AppError.NoError) throw new IOException(op+"失败："+e); }
    public static void Exclusive() {
        CheckLibrary();
        if(Process.GetProcessesByName("REALFORCE Software").Length>0 || Process.GetProcessesByName("REALFORCE CONNECT").Length>0)
            throw new InvalidOperationException("请先退出原 REALFORCE 设置程序和 CONNECT，再读取或保存，避免同时访问设备。");
    }
    public static List<Device> Discover() {
        Exclusive(); var list=new List<Device>();
        var en=new KeyboardEnumurator(@"Topre\REALFORCE_Software\","","",true);
        try { foreach(var entry in en.GetDevicePathList()) {
            var k=new KeyboardEntryR3(); try { Check(k.GetDeviceInfo(entry.DevicePath),"识别设备");
                if(k.ModelNumber.Equals("R3HB11",StringComparison.OrdinalIgnoreCase)) list.Add(new Device { Path=entry.DevicePath,Model=k.ModelNumber });
            } finally { k.HidClose(); }
        } } finally { en.EndPolling(); }
        return list;
    }
    static KeyboardEntryR3 Connect(Device d,bool settingsSession=true) {
        Exclusive(); var k=new KeyboardEntryR3();
        try {
            Check(k.LoadProfile(Path.Combine(Program.RuntimeDirectory,"R30B.r3k")),"加载键盘布局");
            if(settingsSession) Check(k.Open(d.Path),"连接键盘");
            else {
                // Reading must not send App_Start/App_End, which belong to an editing session.
                Check(k.GetDeviceInfo(d.Path),"识别键盘");
                Check(k.HidOpen(d.Path),"连接键盘");
            }
            if(!string.Equals(k.ModelNumber,d.Model,StringComparison.OrdinalIgnoreCase) || k.ModelNumber!="R3HB11") throw new IOException("设备型号发生变化，已停止操作。");
            return k;
        } catch { k.HidClose(); throw; }
    }
    public static byte[] ReadBlock(KeyboardEntryR3 k,int block) {
        byte[] bytes; Check(k.CMD_ReadROM(block,out bytes),"读取设备块 "+block);
        if(bytes==null || bytes.Length!=32) throw new IOException("设备返回了无效长度。"); return bytes;
    }
    public static Snapshot Read(Device d) {
        var k=Connect(d,false);
        try {
            var s=new Snapshot { Device=d,Firmware=k.FirmwareVersion,Time=DateTime.Now };
            byte[] supported; Check(k.R3_GetSupportedFunctionInfo(out supported),"读取支持功能");
            if(supported==null || supported.Length<1) throw new IOException("无法读取设备功能。");
            if((supported[0]&16)!=0) throw new IOException("此版本暂不支持第三代 APC 设备。");
            s.ShortcutCount=(supported[0]&2)!=0?((supported[0]&128)!=0?16:8):0;
            s.CustomEco=(supported[0]&1)!=0;
            var blocks=new List<int>{40,48,49,52}; if(s.ShortcutCount>0) blocks.Add(45); if(s.CustomEco)blocks.Add(54);
            blocks.AddRange(Enumerable.Range(56,40));
            foreach(int b in blocks) { var data=ReadBlock(k,b); s.Original[b]=data; s.Edit[b]=(byte[])data.Clone(); }
            byte[] battery; var batteryResult=k.CMD_ReadRAM(159,out battery);
            s.Battery=batteryResult==LibParam.AppError.NoError && battery!=null && battery.Length>0 && battery[0]<=100?"设备报告 "+battery[0]+"%":"本次未获取有效数值";
            s.Keys=k.KeytopList.Where(x=>x.Type==LibParam.KeytopEntryType.Button && x.Code>=1 && x.Code<=128)
                .Select(x=>new PhysicalKey{Index=x.Code-1,Name=x.Name.Replace("\n"," ").Replace("\r","")}).ToList();
            try {
                s.KeyCounts=ReadCounts(k);s.StatisticsTime=DateTime.Now;
            }catch(IOException ex){s.Notes.Add("按键统计读取失败："+ex.Message);}
            byte[] paired;if(k.CMD_ReadRAM(158,out paired)==LibParam.AppError.NoError && paired!=null && paired.Length==4)s.Pairing=paired;
            byte[] last;if(k.CMD_ReadRAM(160,out last)==LibParam.AppError.NoError && last!=null && last.Length==1)s.LastSlot=last[0];
            for(int i=0;i<4;i++){try{s.SlotNames[i]=Snapshot.DeviceName(ReadBlock(k,41+i));}catch(IOException){s.SlotNames[i]=null;}}
            return s;
        } finally { k.HidClose(); }
    }
    static uint[] ReadCounts(KeyboardEntryR3 k) {
        var counts=new uint[128];
        for(int b=0;b<16;b++){byte[] data;Check(k.CMD_ReadRAM(128+b,out data),"读取按键统计");if(data==null || data.Length!=32)throw new IOException("统计数据长度错误。");for(int i=0;i<8;i++)counts[b*8+i]=Snapshot.CountValue(data,i*4);}
        return counts;
    }
    public static uint[] ClearCounts(Device d) {
        var k=Connect(d);
        try { return ClearAndReadCounts(()=>Check(k.CMD_GeneralRequest(7),"清零按键统计"),()=>ReadCounts(k)); }
        finally{k.Close();}
    }
    public static uint[] ClearAndReadCounts(Action clear,Func<uint[]> read) {
        clear();
        try{return read();}catch(Exception ex){throw new IOException("清零命令已成功，但重新读取统计失败。请按 F5 更新；不要因此重复清零。",ex);}
    }
    public static Snapshot Save(Snapshot s) {
        ValidateSupportedChanges(s.Original,s.Edit);
        if(s.NeedsRefresh) throw new IOException("上次保存未完成，请先刷新设备。");
        if(!s.Dirty) return s;
        var k=Connect(s.Device); bool writing=false;
        try {
            if(k.FirmwareVersion!=s.Firmware) throw new IOException("固件版本发生变化，请重新读取。");
            // Verify every observed setting block, not just the edited blocks.
            VerifyBaseline(s.Original,b=>ReadBlock(k,b));
            string backups=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RealforceAccessible","Backups");
            Directory.CreateDirectory(backups);
            string file=Path.Combine(backups,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".txt");
            File.WriteAllLines(file,new[]{"Model="+s.Device.Model,"Firmware="+s.Firmware,"Original blocks (hex):"}
                .Concat(s.Original.OrderBy(p=>p.Key).Select(p=>p.Key+":"+BitConverter.ToString(p.Value)))
                .Concat(new[]{"Changes:"}).Concat(s.Changes()),Encoding.UTF8);
            CommitBlocks(s.Original,s.Edit,b=>ReadBlock(k,b),(b,data)=>{writing=true;Check(k.CMD_WriteROM(b,data),"写入设置块 "+b);},()=>Check(k.R3_RegistSetting(),"保存到键盘"));
        } catch(Exception e) {
            s.NeedsRefresh=true;
            if(writing) throw new IOException("保存未能完整验证，可能已有部分修改写入。请刷新设备确认实际设置；原始数据备份位于本机 LocalAppData\\RealforceAccessible\\Backups。\r\n"+e.Message,e);
            throw;
        } finally { k.Close(); }
        try { return Read(s.Device); } catch(Exception e) { s.NeedsRefresh=true; throw new IOException("保存与块读回检查已完成，但重新连接读取失败。请按 F5 再次读取。\r\n"+e.Message,e); }
    }
    public static void ValidateSupportedChanges(Dictionary<int,byte[]> original,Dictionary<int,byte[]> edit) {
        foreach(int block in new[]{50,51})if(edit.ContainsKey(block) && (!original.ContainsKey(block) || !edit[block].SequenceEqual(original[block])))
            throw new IOException("R3HB11 不提供指示灯颜色或亮度自定义，已阻止该项写入。");
    }
    public static void VerifyBaseline(Dictionary<int,byte[]> original,Func<int,byte[]> read) {
        foreach(var p in original) if(!read(p.Key).SequenceEqual(p.Value))
            throw new IOException("键盘设置在读取后发生变化。尚未写入，请记录待保存修改，再刷新设备后重新编辑。");
    }
    public static void CommitBlocks(Dictionary<int,byte[]> original,Dictionary<int,byte[]> edit,Func<int,byte[]> read,Action<int,byte[]> write,Action commit) {
        foreach(var p in edit.OrderBy(p=>p.Key)) if(!p.Value.SequenceEqual(original[p.Key])) {
            write(p.Key,p.Value);
            if(!read(p.Key).SequenceEqual(p.Value)) throw new IOException("写入后读回不一致。");
        }
        commit();
        foreach(var p in edit) if(!read(p.Key).SequenceEqual(p.Value)) throw new IOException("保存后核对不一致。");
    }
}

// Framework ComboBox UIA selection events can be ignored by NVDA because the
// provider lacks the matching Selection pattern (dotnet/winforms#7764).
// Keep the native MSAA interface and Windows UIA proxy for these controls only.
sealed class AccessibleComboBox : ComboBox {
    protected override void WndProc(ref Message m) {
        const int WM_GETOBJECT=0x003D;
        const int UiaRootObjectId=-25;
        if(m.Msg==WM_GETOBJECT && unchecked((int)m.LParam.ToInt64())==UiaRootObjectId) {
            m.Result=IntPtr.Zero; return;
        }
        base.WndProc(ref m);
    }
    protected override void OnSelectedIndexChanged(EventArgs e) {
        base.OnSelectedIndexChanged(e);
        if(IsHandleCreated && Focused && !DroppedDown)
            AccessibilityNotifyClients(AccessibleEvents.ValueChange,-1);
    }
}
static class UI {
    public static Button Button(string text,EventHandler click) { var b=new Button { Text=text,AutoSize=true,MinimumSize=new Size(110,32),Margin=new Padding(5) }; b.Click+=click; return b; }
    public static TextBox ReadText(string name,int height) { return new TextBox { AccessibleName=name,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,Height=height,Dock=DockStyle.Fill,TabStop=true }; }
    public static ComboBox Combo(string name,IEnumerable<Choice> choices,int value) {
        var c=new AccessibleComboBox { AccessibleName=name,DropDownStyle=ComboBoxStyle.DropDownList,Width=330 };
        foreach(var p in choices) c.Items.Add(p);
        for(int i=0;i<c.Items.Count;i++) if(((Choice)c.Items[i]).Value==value) c.SelectedIndex=i;
        if(c.SelectedIndex<0) { c.Items.Add(new Choice(value,"保留当前值（"+value+"）")); c.SelectedIndex=c.Items.Count-1; }
        return c;
    }
    public static int Value(ComboBox c) { return ((Choice)c.SelectedItem).Value; }
    public static void Row(TableLayoutPanel p,string label,Control control) {
        int row=p.RowCount++; p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        p.Controls.Add(new Label{Text=label,AutoSize=true,Margin=new Padding(5,10,5,5)},0,row); control.Margin=new Padding(5); p.Controls.Add(control,1,row);
    }
    public static void TextDialog(IWin32Window owner,string title,string text) {
        using(var f=new Form { Text=title,Size=new Size(760,540),StartPosition=FormStartPosition.CenterParent,Font=SystemFonts.MessageBoxFont }) {
            var t=ReadText(title,400); t.Text=text; var close=Button("关闭(&C)",(s,e)=>f.Close()); close.Dock=DockStyle.Bottom;
            f.Controls.Add(t); f.Controls.Add(close); f.CancelButton=close; f.ShowDialog(owner);
        }
    }
    public static DialogResult AskClose(IWin32Window owner,string changes) {
        using(var f=new Form{Text="退出前处理未保存编辑",Size=new Size(700,480),StartPosition=FormStartPosition.CenterParent,Font=SystemFonts.MessageBoxFont}) {
            var summary=ReadText("未保存的编辑",300);summary.Text="以下编辑尚未保存到键盘：\r\n"+changes;
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=60,AccessibleName="退出选择"};
            var save=Button("保存并退出(&S)",(s,e)=>f.DialogResult=DialogResult.Yes);
            var discard=Button("放弃并退出(&D)",(s,e)=>f.DialogResult=DialogResult.No);
            var cancel=Button("取消退出(&C)",(s,e)=>f.DialogResult=DialogResult.Cancel);
            buttons.Controls.Add(save);buttons.Controls.Add(discard);buttons.Controls.Add(cancel);f.Controls.Add(summary);f.Controls.Add(buttons);f.CancelButton=cancel;
            f.Shown+=(s,e)=>cancel.Focus();return f.ShowDialog(owner);
        }
    }
    public static bool ConfirmSave(IWin32Window owner,string changes) {
        using(var f=new Form{Text="确认保存到键盘",Size=new Size(760,560),StartPosition=FormStartPosition.CenterParent,Font=SystemFonts.MessageBoxFont}) {
            var summary=ReadText("逐项配置变更：当前值与目标值",420);
            summary.Text="以下为最近一次读取值与目标值。写入前会再次核对键盘配置，发现外部变化将停止保存。\r\n\r\n"+changes;
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=60};
            var save=Button("确认保存(&S)",(s,e)=>f.DialogResult=DialogResult.OK);
            var cancel=Button("取消(&C)",(s,e)=>f.DialogResult=DialogResult.Cancel);
            buttons.Controls.Add(save);buttons.Controls.Add(cancel);f.Controls.Add(summary);f.Controls.Add(buttons);f.CancelButton=cancel;
            f.Shown+=(s,e)=>{summary.Focus();summary.Select(0,0);};return f.ShowDialog(owner)==DialogResult.OK;
        }
    }
}
sealed class Editor : Form {
    TableLayoutPanel table=new TableLayoutPanel { Dock=DockStyle.Fill,AutoScroll=true,ColumnCount=2,Padding=new Padding(12),AccessibleName="设置项目",TabIndex=0 };
    List<Action> updates=new List<Action>();
    List<Func<bool>> validators=new List<Func<bool>>();
    public Editor(string title) {
        Text=title; Size=new Size(650,550); StartPosition=FormStartPosition.CenterParent; Font=SystemFonts.MessageBoxFont;
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=55,AccessibleName="编辑操作",TabIndex=1};
        var ok=UI.Button("暂存修改(&S)",(s,e)=>{foreach(var valid in validators)if(!valid())return;foreach(var a in updates)a(); DialogResult=DialogResult.OK;});
        var cancel=UI.Button("取消(&C)",(s,e)=>{DialogResult=DialogResult.Cancel;});
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); Controls.Add(table); Controls.Add(buttons); AcceptButton=ok; CancelButton=cancel;
    }
    public void Add(string label,IEnumerable<Choice> choices,int value,Action<int> change) {
        var combo=UI.Combo(label,choices,value); UI.Row(table,label,combo); updates.Add(()=>{int selected=UI.Value(combo);if(selected!=value)change(selected);});
    }
    public void AddName(Snapshot snap) {
        string original=Snapshot.DeviceName(snap.Edit[40]);
        var input=new TextBox{AccessibleName="键盘名称（最多32个英文、数字或半角符号）",Text=original,MaxLength=32,Width=330};
        UI.Row(table,"键盘名称(&N)",input);
        validators.Add(()=>{if(input.Text==original || (input.Text.Length<=32 && input.Text.All(c=>c>=32 && c<=126)))return true;MessageBox.Show(this,"名称最多 32 个英文、数字或半角符号。","名称格式");input.Focus();return false;});
        updates.Add(()=>{if(input.Text!=original)snap.SetName(input.Text);});
    }
    public void AddShortcut(Snapshot snap,int slot) {
        byte mod=(byte)snap.Byte(45,slot*2); var boxes=new List<CheckBox>();
        for(int i=0;i<8;i++) { var c=new CheckBox{Text=Names.Modifiers[i],AutoSize=true,Checked=(mod&(1<<i))!=0}; boxes.Add(c); UI.Row(table,"修饰键",c); }
        var main=UI.Combo("主键",Names.Codes.Where(p=>p.Key<224).Select(p=>new Choice(p.Key,p.Value)),snap.Byte(45,slot*2+1)); UI.Row(table,"主键(&K)",main);
        var preview=new TextBox{AccessibleName="快捷键组合预览",ReadOnly=true,Width=330}; UI.Row(table,"组合预览",preview);
        Action updatePreview=()=>{int m=0;for(int i=0;i<8;i++)if(boxes[i].Checked)m|=1<<i;preview.Text=Names.Shortcut(m,UI.Value(main));};
        foreach(var box in boxes)box.CheckedChanged+=(s,e)=>updatePreview(); main.SelectedIndexChanged+=(s,e)=>updatePreview(); updatePreview();
        updates.Add(()=>{int m=0; for(int i=0;i<8;i++) if(boxes[i].Checked)m|=1<<i; snap.SetByte(45,slot*2,m); snap.SetByte(45,slot*2+1,UI.Value(main));});
    }
}
sealed class MainForm : Form {
    ComboBox devices=new AccessibleComboBox{AccessibleName="设备",DropDownStyle=ComboBoxStyle.DropDownList,Width=220};
    TabControl tabs=new TabControl{Dock=DockStyle.Fill,AccessibleName="设置分区",TabIndex=1};
    TextBox status=UI.ReadText("设备状态",300),details=UI.ReadText("所选按键的设置",180),search=new TextBox{AccessibleName="搜索实体按键",Width=260};
    ListBox keys=new ListBox{Dock=DockStyle.Fill,AccessibleName="实体按键列表",IntegralHeight=false};
    ListBox shortcuts=new ListBox{Dock=DockStyle.Fill,AccessibleName="自定义快捷键列表",IntegralHeight=false};
    ListBox statistics=new ListBox{Dock=DockStyle.Fill,AccessibleName="按键次数（从高到低）",IntegralHeight=false};
    TextBox statsSummary=UI.ReadText("按键统计摘要",60);
    TextBox pairingSummary=UI.ReadText("配对槽位信息",300);
    TextBox message=new TextBox{Dock=DockStyle.Bottom,Height=28,Text="请选择设备并读取。",ReadOnly=true,BorderStyle=BorderStyle.None,AccessibleName="操作状态",TabStop=false};
    Snapshot snapshot; bool busy,closing,exitPrompt,allowClose;
    public MainForm() {
        Text=AppInfo.Title; Size=new Size(980,760); MinimumSize=new Size(760,600); Font=new Font("Microsoft YaHei UI",11); KeyPreview=true;
        using(var icon=Assembly.GetExecutingAssembly().GetManifestResourceStream("assistant.ico"))if(icon!=null)Icon=new Icon(icon);
        var top=new FlowLayoutPanel{Dock=DockStyle.Top,Height=52,AccessibleName="设备连接",TabIndex=0}; top.Controls.Add(new Label{Text="设备(&D)",AutoSize=true,Margin=new Padding(5,12,5,0)}); top.Controls.Add(devices);
        top.Controls.Add(UI.Button("查找设备(&F)",async(s,e)=>await Find())); top.Controls.Add(UI.Button("读取 / 刷新 F5",async(s,e)=>await RefreshDevice()));
        var statePage=new TabPage("设备状态"); statePage.Controls.Add(status); var globals=UI.Button("设备设置：名称 / APC / ECO / A/B 方案(&M)",(s,e)=>EditGlobals()); globals.Dock=DockStyle.Bottom; statePage.Controls.Add(globals);
        var keyPage=new TabPage("逐键设置"); var keyTop=new FlowLayoutPanel{Dock=DockStyle.Top,Height=48,AccessibleName="选择与编辑按键",TabIndex=0}; keyTop.Controls.Add(new Label{Text="搜索按键(&K)",AutoSize=true}); keyTop.Controls.Add(search);
        keyTop.Controls.Add(UI.Button("编辑所选键(&E)",(s,e)=>EditKey()));
        var keyLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,AccessibleName="实体按键及设置",TabIndex=1}; keyLayout.RowStyles.Add(new RowStyle(SizeType.Percent,55)); keyLayout.RowStyles.Add(new RowStyle(SizeType.Percent,45)); keyLayout.Controls.Add(keys,0,0); keyLayout.Controls.Add(details,0,1); keys.TabIndex=0;details.TabIndex=1;
        keyPage.Controls.Add(keyLayout); keyPage.Controls.Add(keyTop);
        var shortcutPage=new TabPage("自定义快捷键"); shortcutPage.Controls.Add(shortcuts); var editShortcut=UI.Button("编辑快捷键(&E)",(s,e)=>EditShortcut()); editShortcut.Dock=DockStyle.Bottom; shortcutPage.Controls.Add(editShortcut);
        tabs.TabPages.AddRange(new[]{statePage,keyPage,shortcutPage});
        var statisticsPage=new TabPage("按键统计");statsSummary.Dock=DockStyle.Top;statsSummary.Height=65;statsSummary.TabIndex=0;statistics.TabIndex=1;statisticsPage.Controls.Add(statistics);statisticsPage.Controls.Add(statsSummary);tabs.TabPages.Add(statisticsPage);
        var clearStats=UI.Button("清零按键统计(&Z)",async(s,e)=>await ClearStatistics());clearStats.Dock=DockStyle.Bottom;clearStats.TabIndex=2;statisticsPage.Controls.Add(clearStats);
        var pairingPage=new TabPage("配对槽位");pairingPage.Controls.Add(pairingSummary);tabs.TabPages.Add(pairingPage);
        var bottom=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,AccessibleName="修改与保存",TabIndex=2};
        bottom.Controls.Add(UI.Button("改动清单(&L)",(s,e)=>ShowChanges())); bottom.Controls.Add(UI.Button("保存到键盘(&S)",async(s,e)=>await Save()));
        bottom.Controls.Add(UI.Button("放弃修改(&R)",(s,e)=>Discard()));
        bottom.Controls.Add(UI.Button("导入配置(&I)",(s,e)=>ImportConfiguration()));
        bottom.Controls.Add(UI.Button("导出配置(&X)",(s,e)=>ExportConfiguration()));
        bottom.Controls.Add(UI.Button("关闭(&C)",(s,e)=>Close()));
        Controls.Add(tabs); Controls.Add(top); Controls.Add(bottom); Controls.Add(message);
        bottom.Controls.Add(UI.Button("关于(&A)",(s,e)=>UI.TextDialog(this,"关于助手",AppInfo.About)));
        search.TextChanged+=(s,e)=>Filter(); keys.SelectedIndexChanged+=(s,e)=>KeyDetails(); keys.DoubleClick+=(s,e)=>EditKey(); shortcuts.DoubleClick+=(s,e)=>EditShortcut();
        keys.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Enter){e.Handled=true;e.SuppressKeyPress=true;EditKey();}};
        shortcuts.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Enter){e.Handled=true;e.SuppressKeyPress=true;EditShortcut();}};
        KeyDown+=async(s,e)=>{if(e.KeyCode==Keys.F5){e.Handled=true;e.SuppressKeyPress=true;await RefreshDevice();}};
        Shown+=async(s,e)=>{await Find();if(!IsDisposed && !Disposing && devices.Items.Count==1)await RefreshDevice();};
        FormClosing+=async(s,e)=>{
            if(allowClose)return;
            if(exitPrompt){e.Cancel=true;return;}
            if(busy){closing=true;e.Cancel=true;message.Text="等待当前设备操作结束后关闭。";return;}
            if(snapshot==null || !snapshot.Dirty)return;
            e.Cancel=true;exitPrompt=true;
            try {
                var choice=UI.AskClose(this,string.Join("\r\n",snapshot.Changes()));
                bool exit=choice==DialogResult.No;
                if(choice==DialogResult.Yes)exit=await Save(false);
                if(exit){allowClose=true;BeginInvoke((Action)Close);}
            }finally{exitPrompt=false;}
        };
    }
    bool ConfirmDiscard() { return snapshot==null || !snapshot.Dirty || MessageBox.Show(this,"有未保存的修改。是否放弃？","未保存修改",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes; }
    void RestoreFocus(Control target) {
        if(IsDisposed || Disposing || !IsHandleCreated)return;
        BeginInvoke((Action)(()=>{
            if(IsDisposed || Disposing || busy || target.IsDisposed || !target.CanSelect)return;
            // Restore the container's active-control chain after all Enabled changes and click events.
            // Do not activate the window: a user may have switched applications during device I/O.
            ActiveControl=target;
            target.Select();
            if(ContainsFocus)target.Focus();
            if(target==status){status.Select(0,0);status.ScrollToCaret();}
        }));
    }
    async Task Work(Func<Task> work,Control focusAfter=null) {
        if(busy)return;
        Control previous=ActiveControl;
        while(previous is ContainerControl && ((ContainerControl)previous).ActiveControl!=null)previous=((ContainerControl)previous).ActiveControl;
        busy=true;
        // Keep a live focus target while the currently focused button/panel is disabled.
        message.Text="正在与键盘通信，请稍候……";
        if(ContainsFocus){ActiveControl=message;message.Focus();}
        tabs.Enabled=false; foreach(Control c in Controls) if(c is FlowLayoutPanel)c.Enabled=false;
        try { await work(); } catch(Exception ex) { message.Text="操作失败；请查看错误信息。"; MessageBox.Show(this,ex.Message,"操作失败",MessageBoxButtons.OK,MessageBoxIcon.Error); }
        finally { busy=false; tabs.Enabled=true; foreach(Control c in Controls) if(c is FlowLayoutPanel)c.Enabled=true; if(closing){closing=false;Close();}else RestoreFocus(focusAfter??previous??devices); }
    }
    async Task Find() {
        if(!ConfirmDiscard())return;
        await Work(async()=>{var ds=await Task.Run(()=>Backend.Discover()); devices.Items.Clear(); foreach(var d in ds)devices.Items.Add(d); snapshot=null; Render();
            if(ds.Count>0)devices.SelectedIndex=0; message.Text=ds.Count==0?"未找到 R3HB 键盘。请检查连接。":"已找到 "+ds.Count+" 台设备。按 F5 读取。";},devices);
    }
    async Task RefreshDevice() {
        if(busy)return; var d=devices.SelectedItem as Device; if(d==null){MessageBox.Show(this,"请先选择设备。");return;} if(!ConfirmDiscard())return;
        int page=tabs.SelectedIndex;
        await Work(async()=>{var fresh=await Task.Run(()=>Backend.Read(d)); snapshot=fresh; Render(); message.Text="已从键盘读取。"; if(page<3)tabs.SelectedIndex=0;},page==3?(Control)statistics:page==4?pairingSummary:status);
    }
    void Render() {
        status.Text=snapshot==null?"尚未读取设备。使用“查找设备”，选择后按 F5。":snapshot.Status();
        status.Select(0,0); status.ScrollToCaret();
        Filter(); int old=shortcuts.SelectedIndex; shortcuts.Items.Clear(); if(snapshot!=null)for(int i=0;i<snapshot.ShortcutCount;i++)shortcuts.Items.Add("快捷键 "+(i+1)+"："+Names.Shortcut(snapshot.Byte(45,i*2),snapshot.Byte(45,i*2+1)));
        if(shortcuts.Items.Count>0)shortcuts.SelectedIndex=Math.Max(0,Math.Min(old,shortcuts.Items.Count-1));
        if(snapshot!=null && snapshot.ShortcutCount==0)shortcuts.Items.Add("设备未报告自定义快捷键支持。");
        statsSummary.Text=snapshot==null?"按 F5 读取按键统计。":snapshot.StatisticsSummary();
        pairingSummary.Text=snapshot==null?"按 F5 读取配对槽位。":snapshot.PairingText();
        statistics.BeginUpdate();statistics.Items.Clear();if(snapshot!=null && snapshot.KeyCounts!=null){int rank=0;foreach(var k in snapshot.RankedKeys())statistics.Items.Add((++rank)+". "+k.Name+"："+snapshot.KeyCounts[k.Index]+" 次");}if(statistics.Items.Count>0)statistics.SelectedIndex=0;statistics.EndUpdate();
        Text=AppInfo.Title+(snapshot!=null && snapshot.Dirty?"（未保存修改）":"");
    }
    void Filter() {
        var old=keys.SelectedItem as PhysicalKey; keys.BeginUpdate(); keys.Items.Clear();
        if(snapshot!=null)foreach(var k in snapshot.Keys.Where(x=>x.Name.IndexOf(search.Text,StringComparison.OrdinalIgnoreCase)>=0))keys.Items.Add(k);
        if(old!=null && keys.Items.Contains(old))keys.SelectedItem=old; else if(keys.Items.Count>0)keys.SelectedIndex=0;
        keys.EndUpdate(); KeyDetails();
    }
    void KeyDetails() { var k=keys.SelectedItem as PhysicalKey; details.Text=k==null?"未选中按键。":snapshot.KeyDetails(k); }
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData) {
        if(keyData==Keys.F1 && !busy){UI.TextDialog(this,"关于助手",AppInfo.About);return true;}
        int page=-1;
        if(keyData==(Keys.Control|Keys.D1))page=0;
        if(keyData==(Keys.Control|Keys.D2))page=1;
        if(keyData==(Keys.Control|Keys.D3))page=2;
        if(keyData==(Keys.Control|Keys.D4))page=3;
        if(keyData==(Keys.Control|Keys.D5))page=4;
        if(keyData==(Keys.Control|Keys.Tab))page=(tabs.SelectedIndex+1)%tabs.TabCount;
        if(keyData==(Keys.Control|Keys.Shift|Keys.Tab))page=(tabs.SelectedIndex+tabs.TabCount-1)%tabs.TabCount;
        if(page>=0 && !busy){tabs.SelectedIndex=page;if(page==0)status.Focus();else if(page==1)search.Focus();else if(page==2)shortcuts.Focus();else if(page==3)statsSummary.Focus();else pairingSummary.Focus();return true;}
        return base.ProcessCmdKey(ref msg,keyData);
    }
    void EditKey() {
        var k=keys.SelectedItem as PhysicalKey; if(snapshot==null || k==null || busy)return;
        using(var f=new Editor("编辑实体键："+k.Name)) {
            var choices=Names.Codes.Where(p=>p.Key<256 || p.Key==4096 || (p.Key>=4320 && p.Key<4320+snapshot.ShortcutCount) || p.Key>=49152).Select(p=>new Choice(p.Key,p.Value)).ToList();
            for(int l=0;l<4;l++){int layer=l; f.Add(Names.Layers[l],choices,snapshot.Mapping(l,k.Index),v=>snapshot.SetMapping(layer,k.Index,v));}
            for(int i=0;i<2;i++){int block=56+i*4; f.Add("逐键 APC 配置 "+(i+1),Enumerable.Range(0,4).Select(n=>new Choice(n,Names.Apc[n])),snapshot.Byte(block,k.Index),v=>snapshot.SetByte(block,k.Index,v));}
            if(f.ShowDialog(this)==DialogResult.OK){Render();message.Text="修改已暂存；点击“保存到键盘”后生效。";}
        }
    }
    void EditGlobals() {
        if(snapshot==null || busy)return;
        using(var f=new Editor("设备模式")) {
            f.AddName(snapshot);
            f.Add("APC 模式",Enumerable.Range(0,6).Select(i=>new Choice(i,Names.Modes[i])),snapshot.Byte(48,0),v=>snapshot.SetByte(48,0,v));
            f.Add("ECO 模式",Enumerable.Range(0,5).Select(i=>new Choice(i,Names.Eco(i))),snapshot.Byte(52,0),v=>snapshot.SetByte(52,0,v));
            f.Add("当前键盘方案 A/B",new[]{new Choice(0,"A"),new Choice(1,"B")},snapshot.Byte(49,0),v=>snapshot.SetByte(49,0,v));
            if(snapshot.CustomEco) {
                int duration=snapshot.Byte(54,0); bool def=duration==0;
                f.Add("自定义 ECO：指示灯",new[]{new Choice(0,"关闭"),new Choice(1,"开启")},def?1:snapshot.Byte(54,1),v=>snapshot.SetCustomEco(1,v));
                f.Add("自定义 ECO：闲置后动作",Enumerable.Range(0,3).Select(i=>new Choice(i,Names.EcoAction(i))),def?1:snapshot.Byte(54,2),v=>snapshot.SetCustomEco(2,v));
                f.Add("自定义 ECO：闲置分钟数",Enumerable.Range(1,18).Select(i=>new Choice(i*10,(i*10)+" 分钟")),def?30:duration,v=>snapshot.SetCustomEco(0,v));
            }
            if(f.ShowDialog(this)==DialogResult.OK)Render();
        }
    }
    void EditShortcut() {
        int i=shortcuts.SelectedIndex; if(snapshot==null || i<0 || i>=snapshot.ShortcutCount || busy)return;
        using(var f=new Editor("编辑快捷键 "+(i+1))) { f.AddShortcut(snapshot,i); if(f.ShowDialog(this)==DialogResult.OK)Render(); }
    }
    void ShowChanges() { UI.TextDialog(this,"待保存的改动",snapshot==null || !snapshot.Dirty?"没有待保存的修改。":string.Join("\r\n",snapshot.Changes())); }
    async Task ClearStatistics() {
        if(snapshot==null || busy)return;
        if(MessageBox.Show(this,"将立即清零键盘中的所有按键统计，无法撤销。不会保存或放弃助手中的配置编辑。是否继续？","清零按键统计",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
        await Work(async()=>{try{snapshot.KeyCounts=await Task.Run(()=>Backend.ClearCounts(snapshot.Device));snapshot.StatisticsTime=DateTime.Now;message.Text="清零命令已完成，已重新读取；操作期间的新按键可能已重新计数。";}catch{snapshot.KeyCounts=null;throw;}finally{Render();}},statistics);
    }
    async Task<bool> Save(bool confirm=true) {
        if(snapshot==null || busy || !snapshot.Dirty){if(confirm)MessageBox.Show(this,"没有待保存的修改。");return false;}
        string changes=string.Join("\r\n",snapshot.Changes());
        if(confirm && !UI.ConfirmSave(this,changes))return false;
        bool saved=false;
        await Work(async()=>{try{snapshot=await Task.Run(()=>Backend.Save(snapshot));saved=true;}finally{Render();}message.Text="已保存并重新读取核对。";if(confirm)MessageBox.Show(this,"已保存到键盘，设备读回数据与设置一致。","保存完成");});
        return saved;
    }
    void Discard() { if(snapshot==null || !ConfirmDiscard())return; foreach(var p in snapshot.Original)snapshot.Edit[p.Key]=(byte[])p.Value.Clone();Render(); }
    bool ConfigurationReady() {
        if(busy)return false;
        if(snapshot==null || snapshot.NeedsRefresh){MessageBox.Show(this,"请先按 F5 成功读取键盘配置。","配置文件");return false;}
        return true;
    }
    void ExportConfiguration() {
        if(!ConfigurationReady())return;
        using(var d=new SaveFileDialog{Filter="助手配置文件 (*.json)|*.json",DefaultExt="json",FileName="REALFORCE-R3HB11-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json"}) {
            if(d.ShowDialog(this)!=DialogResult.OK)return;
            try { File.WriteAllText(d.FileName,ConfigurationFile.Encode(snapshot),Encoding.UTF8);message.Text="配置已导出，包含助手当前的待保存编辑。";MessageBox.Show(this,"配置已导出。包含当前编辑值，未向键盘写入。","导出完成"); }
            catch(Exception ex){MessageBox.Show(this,ex.Message,"导出失败");}
        }
    }
    void ImportConfiguration() {
        if(!ConfigurationReady())return;
        using(var d=new OpenFileDialog{Filter="助手配置文件 (*.json)|*.json",CheckFileExists=true}) {
            if(d.ShowDialog(this)!=DialogResult.OK)return;
            try {
                var ignored=new List<string>();var imported=ConfigurationFile.Read(d.FileName,snapshot,ignored);
                if(snapshot.Dirty && MessageBox.Show(this,"导入将替换助手中尚未保存的配置编辑。是否继续？","导入配置",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
                snapshot.Edit=imported;Render();
                message.Text=snapshot.Dirty?"配置已导入，尚未写入键盘。请检查改动清单后保存。":"导入配置与上次读取值相同，没有待保存的修改。";
                UI.TextDialog(this,"导入结果：尚未写入键盘",message.Text+"\r\n\r\n"+string.Join("\r\n",snapshot.Changes())+(ignored.Count==0?"":"\r\n\r\n已忽略非可修改字段：\r\n"+string.Join("\r\n",ignored)));RestoreFocus(status);
            }catch(Exception ex){MessageBox.Show(this,ex.Message,"导入失败，现有编辑已保留");}
        }
    }
}
static class Checks {
    static void Assert(bool yes,string message) { if(!yes)throw new Exception(message); }
    public static void Run() {
        var s=new Snapshot{Device=new Device{Model="R3HB11"},Firmware="test",ShortcutCount=8};
        foreach(int b in new[]{45,48,49,52}.Concat(Enumerable.Range(56,40))) {s.Original[b]=new byte[32];s.Edit[b]=new byte[32];}
        s.Keys.Add(new PhysicalKey{Index=16,Name="test key"});
        s.SetMapping(3,16,4327); Assert(s.Mapping(3,16)==4327,"map roundtrip"); Assert(s.Edit[89][0]==16 && s.Edit[89][1]==231,"big endian/block boundary");
        Assert(s.Original[89][0]==0,"original mutation"); Assert(s.Dirty,"dirty detection");
        s.SetByte(56,127,3); Assert(s.Edit[59][31]==3,"APC boundary");
        s.SetByte(45,14,129);s.SetByte(45,15,4); Assert(Names.Shortcut(129,4)=="左 Ctrl + 右 Win + A","modifier decode");
        Assert(s.Changes().Count==2,"change list");
        s.Original[54]=new byte[32];s.Edit[54]=new byte[32];s.SetCustomEco(1,0);
        Assert(s.Byte(54,0)==30 && s.Byte(54,1)==0 && s.Byte(54,2)==1,"custom ECO default normalization");
        var original=new Dictionary<int,byte[]>{{48,new byte[32]},{64,new byte[32]}};
        var edit=original.ToDictionary(p=>p.Key,p=>(byte[])p.Value.Clone()); edit[64][0]=7;
        var fake=original.ToDictionary(p=>p.Key,p=>(byte[])p.Value.Clone()); int writes=0,commits=0;
        Backend.VerifyBaseline(original,b=>fake[b]);
        Backend.CommitBlocks(original,edit,b=>fake[b],(b,v)=>{writes++;fake[b]=(byte[])v.Clone();},()=>commits++);
        Assert(writes==1 && commits==1 && fake[48].All(v=>v==0),"only changed blocks committed");
        bool rejected=false;try{Backend.VerifyBaseline(original,b=>fake[b]);}catch(IOException){rejected=true;}Assert(rejected,"external change detection");
        rejected=false;commits=0;try{Backend.CommitBlocks(original,edit,b=>original[b],(b,v)=>{},()=>commits++);}catch(IOException){rejected=true;}
        Assert(rejected && commits==0,"readback mismatch must prevent commit");
        rejected=false;try{Backend.CommitBlocks(original,edit,b=>fake[b],(b,v)=>{throw new IOException("simulated write error");},()=>commits++);}catch(IOException){rejected=true;}
        Assert(rejected && commits==0,"write errors must prevent commit");
        Assert(Snapshot.CountValue(new byte[]{255,255,255,254},0)==4294967294u,"unsigned big endian statistics");
        var extra=new Snapshot{Keys=new List<PhysicalKey>{new PhysicalKey{Index=2,Name="C"},new PhysicalKey{Index=1,Name="B"},new PhysicalKey{Index=0,Name="A"}},KeyCounts=new uint[]{3,9,9}};
        Assert(extra.RankedKeys().Select(k=>k.Index).SequenceEqual(new[]{1,2,0}),"statistics descending and stable ties");
        extra.Original[40]=new byte[32];extra.Edit[40]=new byte[32];extra.SetName("Desk-R3");Assert(Snapshot.DeviceName(extra.Edit[40])=="Desk-R3" && extra.Original[40][0]==0,"name encoding and snapshot isolation");
        rejected=false;try{extra.SetName("中文");}catch(ArgumentException){rejected=true;}Assert(rejected && Snapshot.DeviceName(extra.Edit[40])=="Desk-R3","reject unsupported name without mutation");
        int clearCalls=0,readCalls=0;
        var counted=Backend.ClearAndReadCounts(()=>clearCalls++,()=>{readCalls++;return new uint[]{2};});
        Assert(clearCalls==1 && readCalls==1 && counted[0]==2,"clear reads actual counts, not assumed zero");
        rejected=false;try{Backend.ClearAndReadCounts(()=>{throw new IOException("clear rejected");},()=>{readCalls++;return null;});}catch(IOException){rejected=true;}
        Assert(rejected && readCalls==1,"failed clear prevents readback");
        rejected=false;try{Backend.ClearAndReadCounts(()=>clearCalls++,()=>{throw new IOException("read failed");});}catch(IOException ex){rejected=ex.Message.Contains("不要因此重复清零");}
        Assert(rejected && clearCalls==2,"clear readback failure does not retry mutation");
        var ledOriginal=new Dictionary<int,byte[]>{{50,new byte[32]},{51,new byte[32]}};
        var ledEdit=ledOriginal.ToDictionary(p=>p.Key,p=>(byte[])p.Value.Clone());
        Backend.ValidateSupportedChanges(ledOriginal,ledEdit);
        foreach(int block in new[]{50,51}){ledEdit[block][0]=1;rejected=false;try{Backend.ValidateSupportedChanges(ledOriginal,ledEdit);}catch(IOException){rejected=true;}Assert(rejected,"reject unsupported LED block "+block);ledEdit[block][0]=0;}
        extra.Device=new Device{Path="USB",Model="R3HB11"};extra.Pairing=new byte[]{0,1,0,1};extra.LastSlot=3;extra.SlotNames=new[]{"stale","Laptop","stale","Desktop"};
        string paired=extra.PairingText();Assert(paired.Contains("槽位 1：未配对") && !paired.Contains("stale") && paired.Contains("最近连接的蓝牙槽位：4") && paired.Contains("当前连接：USB"),"pairing empty slots and last/current distinction");
        using(var f=new MainForm()) { Assert(f.Controls.Count>0,"UI creation"); }
        using(var f=new Editor("test")){f.AddShortcut(s,0);Assert(f.AcceptButton!=null && f.CancelButton!=null,"dialog keyboard buttons");}
        s.Original[40]=new byte[32];s.Edit[40]=new byte[32];s.SetName("Config & Test");s.CustomEco=true;
        string config=ConfigurationFile.Encode(s);
        var imported=ConfigurationFile.Decode(config,s);
        Assert(imported.All(p=>p.Value.SequenceEqual(s.Edit[p.Key])),"configuration JSON roundtrip");
        imported[48][0]=5;Assert(s.Edit[48][0]==0 && s.Original[48][0]==0,"import isolates baseline and editor");
        s.Edit[48][31]=123;imported=ConfigurationFile.Decode(config,s);Assert(imported[48][31]==123,"import preserves destination reserved bytes");
        string compact=ConfigurationFile.Encode(s,false);
        foreach(string invalid in new[]{compact.Replace("R3HB11","R3HB12"),compact.Replace("\"firmware\":\"test\"","\"firmware\":\"other\""),compact.Replace("\"formatVersion\":1","\"formatVersion\":99"),compact.Replace("\"apc\":0","\"apc\":6"),compact.Replace("\"eco\":0,",""),compact.Replace("\"apc\":0","\"apc\":0,\"apc\":1"),compact.Replace("\"apc\":0","\"apc\":\"0\""),compact.Replace("\"apc\":0","\"apc\":null"),compact.Replace("\"apc\":0","\"apc\":1.5"),compact.Replace("\"apc\":0","\"apc\":-1"),compact.Replace("\"key.16.layer.0\":0","\"key.16.layer.0\":65535"),compact+" garbage"}) {
            rejected=false;try{ConfigurationFile.Decode(invalid,s);}catch(Exception){rejected=true;}
            Assert(rejected && s.Edit[48][0]==0 && s.Edit[48][31]==123,"invalid import rejected atomically");
        }
        s.Edit[49][0]=1; var ignored=new List<string>();imported=ConfigurationFile.Decode(compact.Replace("\"settings\":{","\"battery\":99,\"settings\":{\"map\":0,\"pairing\":[1,2],\"rom.50\":255,"),s,ignored);
        Assert(ignored.Count==4 && imported[49][0]==1 && imported.All(p=>p.Value.SequenceEqual(s.Edit[p.Key])),"read-only and unknown fields ignored without importing reserved memory");
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test.txt"),"PASS: configuration roundtrip, reserved byte preservation, baseline isolation and malformed/incompatible import rejection; big-endian mapping; block boundaries; dirty detection; shortcut decoding; external change rejection; readback mismatch rejection; write error propagation; statistics sorting; name validation; UI construction.\r\nNo device I/O performed.");
    }
}
}
