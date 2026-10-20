using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using RFSWLib;
class RawStatus : KeyboardEntryR3 {
    public void StatusPacket() {
        byte[] request=new byte[65]; request[1]=170;request[2]=170;request[3]=195;request[5]=1;request[6]=152;
        byte[] response; var error=RealForceIoControl("ReadObservedStatus152",request,out response,1);
        Console.WriteLine("RAW152 "+error+" "+BitConverter.ToString(response));
    }
}
class LiveProbe {
    static int Main(string[] args) {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        try { return Run(args); } catch(Exception ex) { Console.WriteLine(ex);return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Run(string[] args) {
        var en=new KeyboardEnumurator(@"Topre\REALFORCE_Software\","","",true);
        try { foreach(var d in en.GetDevicePathList()) {
            var k=new RawStatus();
            try {
                var error=k.HidOpen(d.DevicePath); if(error!=LibParam.AppError.NoError)throw new IOException(error.ToString());
                string model,fw,cmp; error=k.CMD_GetDeviceInfo(out model,out fw,out cmp);Console.WriteLine("Model="+model+" Firmware="+fw+" Result="+error);
                if(model!="R3HB11")continue;
                if(Array.IndexOf(args,"--session")>=0) {
                    error=k.CMD_StartEnd(RealforceDevice.eAppStEd.App_Start);
                    Console.WriteLine("App_Start "+error);
                }
                k.StatusPacket();
                foreach(int b in new[]{152,154}) { byte[] data; error=k.CMD_ReadRAM(b,out data);Console.WriteLine("RAM "+b+" "+error+" "+BitConverter.ToString(data)); }
                foreach(int b in new[]{48,49,52}) { byte[] data; error=k.CMD_ReadROM(b,out data);Console.WriteLine("ROM "+b+" "+error+" "+BitConverter.ToString(data)); }
                foreach(int b in new[]{0,1}) { byte[] data; error=k.CMD_ReadRAM(b,out data);Console.WriteLine("KEY "+b+" "+error+" "+BitConverter.ToString(data)); }
            } finally {
                if(Array.IndexOf(args,"--session")>=0 && k.HidIsOpened())
                    Console.WriteLine("App_End "+k.CMD_StartEnd(RealforceDevice.eAppStEd.App_End));
                k.HidClose();
            }
        } } finally { en.EndPolling(); }
        return 0;
    }
}
