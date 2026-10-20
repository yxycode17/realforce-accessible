using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RFSWLib;

namespace RealforceAccessible {
sealed class FirmwareActivity : IDisposable {
    readonly Mutex mutex;bool owned;
    public FirmwareActivity() {
        mutex=new Mutex(false,"Local\\RealforceAccessibleFirmwareUpdate");
        try{try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
            if(!owned)throw new IOException("另一个助手实例正在更新固件，请等待完成。");
        }catch{mutex.Dispose();throw;}
    }
    public void Dispose(){try{if(owned){owned=false;mutex.ReleaseMutex();}}finally{mutex.Dispose();}}
    public static void CheckIdle(){using(var check=new FirmwareActivity()){} }
}
sealed class FirmwareIdentity {
    public string Model,Version,Compare,Hardware;
    public bool Same(FirmwareIdentity other) { return Model==other.Model && Compare==other.Compare && Hardware==other.Hardware; }
}
sealed class FirmwareImage {
    readonly byte[] payload;
    public readonly ushort Crc;
    public readonly string TargetVersion,Sha256;
    public string TargetVersionText { get { return string.Format("{0:X}.{1:X}{2:X}",Convert.ToByte(TargetVersion.Substring(0,2),16),Convert.ToByte(TargetVersion.Substring(2,2),16),Convert.ToByte(TargetVersion.Substring(4,2),16)); } }
    public int Length { get { return payload.Length; } }
    FirmwareImage(byte[] bytes) {
        payload=bytes;Crc=LibUtil.calc_crc(bytes,bytes.Length);
        int tail=bytes.Length-16;TargetVersion=BitConverter.ToString(bytes,tail+9,3).Replace("-","");
        using(var hash=SHA256.Create())Sha256=BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","");
    }
    public static FirmwareImage Parse(byte[] file) {
        if(file==null || file.Length<65538 || file.Length>1048578)throw new IOException("固件文件长度无效：应为 65538 至 1048578 字节。");
        var bytes=file.Skip(2).ToArray();var image=new FirmwareImage(bytes);
        if((file[0]|(file[1]<<8))!=image.Crc)throw new IOException("固件 CRC 校验失败，文件可能损坏。");
        return image;
    }
    public static FirmwareImage Read(string path) {
        using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            if(f.Length<65538 || f.Length>1048578)throw new IOException("固件文件大小无效。");
            byte[] data=new byte[(int)f.Length];int offset=0,n;
            while(offset<data.Length && (n=f.Read(data,offset,data.Length-offset))>0)offset+=n;
            if(offset!=data.Length)throw new IOException("未能完整读取固件文件。");return Parse(data);
        }
    }
    public void Validate(FirmwareIdentity device) {
        if(!Device.IsSupported(device.Model) || device.Hardware==null || device.Hardware.Length!=10 || device.Compare==null || device.Compare.Length!=6)
            throw new IOException("无法确认设备的型号、硬件标识或固件版本。");
        byte[] expected=new byte[8];
        for(int i=0;i<6;i++)expected[i]=checked((byte)device.Hardware[i]);
        expected[6]=Convert.ToByte(device.Hardware.Substring(6,2),16);expected[7]=Convert.ToByte(device.Hardware.Substring(8,2),16);
        int tail=payload.Length-16;
        // Match the original selector: '#' is a wildcard; final hardware byte is ignored.
        for(int i=0;i<7;i++)if(payload[tail+i]!=35 && payload[tail+i]!=expected[i])throw new IOException("固件文件不适用于当前键盘型号或硬件版本。");
        if(string.Compare(TargetVersion,device.Compare,StringComparison.OrdinalIgnoreCase)<0)throw new IOException("目标固件版本低于当前版本，不允许降级。");
    }
    public byte[] Chunk(int offset) { return payload.Skip(offset).Take(Math.Min(57,Length-offset)).ToArray(); }
}
interface IFirmwareTransport : IDisposable {
    FirmwareIdentity Identity();
    void Reboot();
    void Reconnect(Action<string> progress);
    void Start(int length,ushort crc);
    void Send(int index,byte[] bytes);
    void End();
}
// Disable the library's generic command retransmission for this session: an
// ambiguous flash write must stop rather than silently be sent again.
sealed class FirmwareTransport : KeyboardEntryR3,IFirmwareTransport {
    readonly string path;readonly int oldRetries;bool disposed;
    public FirmwareTransport(string devicePath) {
        path=devicePath;oldRetries=RetryMaxCount;RetryMaxCount=1;SetLongTimeout(true);
        try{Backend.Check(HidOpen(path),"打开升级通信会话");}catch{Dispose();throw;}
    }
    public FirmwareIdentity Identity() {
        string model,version,compare,hardware;
        Backend.Check(CMD_GetDeviceInfo(out model,out version,out compare),"读取固件版本");
        Backend.Check(R3_GetInfo(R3_FirmwareInfo.ModelNumber,out hardware,true),"读取硬件标识");
        return new FirmwareIdentity{Model=model,Version=version,Compare=compare,Hardware=hardware};
    }
    public void Reboot() {
        var result=CMD_RebootForUpdate();
        // The original updater accepts DevceRead when reboot interrupts the reply.
        if(result!=LibParam.AppError.NoError && result!=LibParam.AppError.DevceRead)Backend.Check(result,"进入固件升级模式");
        HidClose();
    }
    public void Reconnect(Action<string> progress) {
        HidClose();
        for(int i=10;i>0;i--){progress("等待键盘重新连接："+i+" 秒");Thread.Sleep(1000);}
        var watch=Stopwatch.StartNew();
        do {
            Backend.Check(HidOpen(path),"重新打开设备会话");
            // HidOpen alone only opens the logical session; verify actual OS handle.
            var result=HidOpenStream();HidCloseStream();
            if(result==LibParam.AppError.NoError){Thread.Sleep(500);return;}
            HidClose();Thread.Sleep(500);
        }while(watch.ElapsedMilliseconds<20000);
        throw new IOException("等待原 USB 设备路径重新连接超时。未尝试连接其他设备。");
    }
    public void Start(int length,ushort crc){Backend.Check(CMD_StartUpdate(length,crc),"开始固件传输");HidWait();}
    public void Send(int index,byte[] bytes){Backend.Check(CMD_SendFwData(index,bytes),"传输固件包 "+index+" 并核对返回序号");}
    public void End(){Backend.Check(CMD_EndUpdate(),"结束固件更新");HidWait();HidClose();}
    public void Dispose(){if(disposed)return;disposed=true;try{HidClose();}finally{SetLongTimeout(false);RetryMaxCount=oldRetries;}}
}
static class FirmwareEngine {
    public static void Run(FirmwareImage image,FirmwareIdentity expected,IFirmwareTransport device,Action<string> progress) {
        string stage="更新前身份复核";
        try {
            var current=device.Identity();if(!current.Same(expected))throw new IOException("设备身份或固件版本在确认后发生变化。");
            image.Validate(current);
            stage="重启进入升级模式";progress(stage);device.Reboot();
            stage="升级前重连";progress(stage);device.Reconnect(progress);
            stage="开始传输";progress(stage);device.Start(image.Length,image.Crc);
            int index=0,last=-1;
            for(int offset=0;offset<image.Length;offset+=57) {
                stage="传输固件包 "+index;device.Send(index++,image.Chunk(offset));
                int percent=Math.Min(100,(offset+57)*100/image.Length);
                if(percent!=last){last=percent;progress("固件传输："+percent+"%");}
            }
            stage="结束升级";progress(stage);device.End();
            stage="升级后重连";progress(stage);device.Reconnect(progress);
            stage="升级后型号及版本核对";progress(stage);var actual=device.Identity();
            if(actual.Model!=expected.Model || actual.Hardware!=expected.Hardware || actual.Compare!=image.TargetVersion)
                throw new IOException("重连后的型号、硬件标识或固件版本不符合预期；不能确认更新成功。");
            progress("固件版本核对通过。");
        }catch(Exception ex){throw new IOException("失败阶段："+stage+"\r\n"+ex.Message+"\r\n升级流程已停止，不会自动重试刷写。",ex);}
    }
}
sealed class FirmwarePlan {
    public Device Device;public FirmwareIdentity Identity;public FirmwareImage Image;public Snapshot Snapshot;public string FileName;
    public static FirmwarePlan Prepare(Device device,string path) {
        if(KeyboardEnumurator.isBleConnect(device.Path))throw new IOException("固件更新只支持 USB 连接。");
        var image=FirmwareImage.Read(path);CheckSingle(device);
        var snapshot=Backend.Read(device);FirmwareIdentity identity;
        using(var transport=new FirmwareTransport(device.Path))identity=transport.Identity();
        if(identity.Model!=device.Model || identity.Version!=snapshot.Firmware)throw new IOException("读取期间设备或版本发生变化，请重新读取。");
        image.Validate(identity);
        return new FirmwarePlan{Device=device,Identity=identity,Image=image,Snapshot=snapshot,FileName=Path.GetFileName(path)};
    }
    public static void CheckSingle(Device device) {
        Backend.Exclusive();var devices=Backend.Discover();
        if(devices.Count!=1 || devices[0].Path!=device.Path || devices[0].Model!=device.Model)
            throw new IOException("更新时只允许连接一台受支持的 R3HB 键盘，且必须与选择的设备一致。");
    }
    public string Summary() {
        return "型号："+Identity.Model+"\r\n当前版本："+Identity.Version+"（比较码 "+Identity.Compare+"）\r\n目标版本数值（不含设备前缀）："+Image.TargetVersionText+"（比较码 "+Image.TargetVersion+"）"+
            (Image.TargetVersion==Identity.Compare?"（与当前版本相同，将重新刷写）":"")+
            "\r\n固件文件："+FileName+"\r\n固件数据长度："+Image.Length+" 字节\r\nCRC："+Image.Crc.ToString("X4")+
            "\r\n\r\n已通过文件完整性、型号及版本校验。请仅使用来源可信的官方固件；CRC 不是数字签名。\r\n此升级实现尚未实机刷写验证。更新会重启键盘并重新连接；开始后不能取消。请勿拔线、关机或断电。";
    }
}
sealed class FirmwareDialog : Form {
    readonly FirmwarePlan plan;readonly TextBox output;readonly Button start,close;
    bool running;public bool Attempted,Completed;
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ShutdownBlockReasonCreate(IntPtr hwnd,string reason);
    [DllImport("user32.dll",SetLastError=true)] static extern bool ShutdownBlockReasonDestroy(IntPtr hwnd);
    public FirmwareDialog(FirmwarePlan value) {
        plan=value;Text="固件更新：文件校验已通过";Size=new Size(820,620);StartPosition=FormStartPosition.CenterParent;Font=SystemFonts.MessageBoxFont;
        output=UI.ReadText("固件更新信息和进度",460);output.Text=plan.Summary();
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=60};
        start=UI.Button("开始更新(&U)",async(s,e)=>await Start());close=UI.Button("关闭(&C)",(s,e)=>Close());
        buttons.Controls.Add(start);buttons.Controls.Add(close);Controls.Add(output);Controls.Add(buttons);CancelButton=close;
        Shown+=(s,e)=>{output.Focus();output.Select(0,0);};
        FormClosing+=(s,e)=>{if(running){e.Cancel=true;MessageBox.Show(this,"键盘固件更新正在进行。请等待完成，勿关闭助手或电脑。","更新进行中",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
    }
    protected override void WndProc(ref Message m) {
        if(m.Msg==0x11 && running){m.Result=IntPtr.Zero;return;} // WM_QUERYENDSESSION: Windows displays the registered reason.
        base.WndProc(ref m);
    }
    async Task Start() {
        if(running)return;
        if(MessageBox.Show(this,plan.Summary()+"\r\n\r\n确定现在更新键盘固件吗？","再次确认固件更新",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
        running=true;start.Enabled=false;close.Enabled=false;output.Focus();
        string folder=null;bool power=false,shutdown=false;
        try {
            power=SetThreadExecutionState(0x80000001)!=0;
            if(!power)throw new IOException("无法启用升级期间的防休眠保护，未开始升级。");
            shutdown=ShutdownBlockReasonCreate(Handle,"REALFORCE 键盘固件更新正在进行，请等待完成后再关机或注销。");
            if(!shutdown)throw new IOException("无法注册关机警告，未开始升级。");
            folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RealforceAccessible","FirmwareUpdates",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
            Directory.CreateDirectory(folder);
            var progress=new Progress<string>(line=>{if(IsDisposed)return;output.AppendText("\r\n"+line);output.SelectionStart=output.TextLength;output.ScrollToCaret();});
            await Task.Run(()=>{
                using(var activity=new FirmwareActivity()) {
                FirmwarePlan.CheckSingle(plan.Device);
                var fresh=Backend.Read(plan.Device);
                Backend.VerifyBaseline(plan.Snapshot.Original,b=>fresh.Original[b]);
                File.WriteAllText(Path.Combine(folder,"configuration.json"),ConfigurationFile.Encode(fresh),Encoding.UTF8);
                File.WriteAllText(Path.Combine(folder,"settings-raw.txt"),string.Join("\r\n",fresh.Original.OrderBy(p=>p.Key).Select(p=>p.Key+":"+Convert.ToBase64String(p.Value))),Encoding.UTF8);
                using(var log=new StreamWriter(Path.Combine(folder,"update.log"),false,Encoding.UTF8)) {
                    log.AutoFlush=true;log.WriteLine(plan.Summary());log.WriteLine("Payload SHA256: "+plan.Image.Sha256);
                    Action<string> report=line=>{log.WriteLine(DateTime.Now.ToString("o")+" "+line);((IProgress<string>)progress).Report(line);};
                    using(var transport=new FirmwareTransport(plan.Device.Path)) {
                        Attempted=true;
                        try{FirmwareEngine.Run(plan.Image,plan.Identity,transport,report);}
                        catch(Exception ex){try{log.WriteLine(ex.ToString());}catch{}throw;}
                    }
                }
                }
            });
            Completed=true;output.AppendText("\r\n更新完成，型号及固件版本已核对。");
        }catch(Exception ex){
            string text=ex.Message+(Attempted?"\r\n如果键盘不能正常重新连接，请使用原版升级工具及官方恢复说明处理；助手不会自动重刷。":"\r\n尚未开始固件升级。")+(folder==null?"":"\r\n配置备份与日志目录："+folder);
            output.AppendText("\r\n更新未完成："+text);MessageBox.Show(this,text,"固件更新失败",MessageBoxButtons.OK,MessageBoxIcon.Error);
        }finally{
            if(shutdown)ShutdownBlockReasonDestroy(Handle);if(power)SetThreadExecutionState(0x80000000);
            running=false;close.Enabled=true;close.Focus();
        }
        if(Completed)Close();
    }
}
}
