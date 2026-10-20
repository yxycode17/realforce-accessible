using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RFSWLib;

namespace RealforceAccessible {
static class FirmwareChecks {
    static void Assert(bool value,string message){if(!value)throw new Exception("Firmware test: "+message);}
    static FirmwareIdentity Identity(string compare="000107") {return new FirmwareIdentity{Model="R3HB11",Hardware="R3HB111000",Version="A0.17",Compare=compare};}
    static byte[] FileBytes(int length=65536) {
        var file=new byte[length+2];for(int i=2;i<file.Length;i++)file[i]=(byte)(i%251);
        int tail=file.Length-16;Encoding.ASCII.GetBytes("R3HB11").CopyTo(file,tail);file[tail+6]=0x10;file[tail+7]=0;
        file[tail+9]=0;file[tail+10]=1;file[tail+11]=8;Checksum(file);return file;
    }
    static void Checksum(byte[] file){ushort crc=LibUtil.calc_crc(file.Skip(2).ToArray(),file.Length-2);file[0]=(byte)crc;file[1]=(byte)(crc>>8);}
    static void Reject(Action action,string description){bool rejected=false;try{action();}catch{rejected=true;}Assert(rejected,description);}
    sealed class Fake : IFirmwareTransport {
        public readonly List<string> Calls=new List<string>();
        public readonly List<byte> Data=new List<byte>();
        public string Failure;public bool BadInitial,BadFinal;int identities,packets,reconnects;
        void Call(string name){Calls.Add(name);if(Failure==name)throw new IOException("simulated "+name);}
        public FirmwareIdentity Identity(){Call(++identities==1?"identity-before":"identity-after");var id=FirmwareChecks.Identity(identities==1?"000107":"000108");if(BadInitial && identities==1)id.Hardware="R3HB111100";if(BadFinal && identities==2)id.Compare="000107";return id;}
        public void Reboot(){Call("reboot");}
        public void Reconnect(Action<string> progress){Call("reconnect-"+(++reconnects));}
        public void Start(int length,ushort crc){Call("start");Assert(length==65536,"payload length excludes file CRC");}
        public void Send(int index,byte[] data){Call("packet-"+index);Assert(index==packets++,"packet ordering");Assert(data.Length>=1 && data.Length<=57,"packet length");Data.AddRange(data);}
        public void End(){Call("end");}
        public void Dispose(){Call("dispose");}
    }
    public static void Run() {
        using(var activity=new FirmwareActivity()) {
            FirmwareActivity.CheckIdle();
            bool blocked=System.Threading.Tasks.Task.Run(()=>{try{FirmwareActivity.CheckIdle();return false;}catch(IOException){return true;}}).GetAwaiter().GetResult();
            Assert(blocked,"concurrent device operation blocked during firmware activity");
        }
        FirmwareActivity.CheckIdle();
        var file=FileBytes();var image=FirmwareImage.Parse(file);image.Validate(Identity());
        Assert(image.TargetVersion=="000108" && image.Length==65536,"footer version and payload size");
        Assert(LibUtil.calc_crc(Encoding.ASCII.GetBytes("123456789"),9)==0x6F91,"CRC test vector");
        Reject(()=>FirmwareImage.Parse(new byte[65537]),"too short");Reject(()=>FirmwareImage.Parse(new byte[1048579]),"too large");
        var bad=(byte[])file.Clone();bad[2]^=1;Reject(()=>FirmwareImage.Parse(bad),"bad CRC");
        Assert(FirmwareImage.Parse(FileBytes(1048576)).Length==1048576,"maximum file size");
        Reject(()=>image.Validate(Identity("000109")),"downgrade rejected");image.Validate(Identity("000108"));
        var wrong=Identity();wrong.Hardware="R3HB131000";Reject(()=>image.Validate(wrong),"wrong model footer");
        wrong=Identity();wrong.Hardware="R3HB111100";Reject(()=>image.Validate(wrong),"wrong hardware byte");
        var wildcard=(byte[])file.Clone();wildcard[wildcard.Length-16+4]=35;wildcard[wildcard.Length-16+7]=255;Checksum(wildcard);FirmwareImage.Parse(wildcard).Validate(Identity());
        var fake=new Fake();FirmwareEngine.Run(image,Identity(),fake,s=>{});
        Assert(fake.Data.SequenceEqual(file.Skip(2)),"all payload bytes sent exactly once");
        Assert(string.Join(",",fake.Calls.Take(4))=="identity-before,reboot,reconnect-1,start","startup sequence");
        Assert(string.Join(",",fake.Calls.Skip(fake.Calls.Count-3))=="end,reconnect-2,identity-after","completion verified after reconnect");
        foreach(string stage in new[]{"identity-before","reboot","reconnect-1","start","packet-0","packet-7","end","reconnect-2","identity-after"}) {
            fake=new Fake{Failure=stage};Reject(()=>FirmwareEngine.Run(image,Identity(),fake,s=>{}),"error surfaced "+stage);
            Assert(fake.Calls.Last()==stage && fake.Calls.Count(c=>c==stage)==1,"stop immediately and do not retry "+stage);
        }
        fake=new Fake{BadInitial=true};Reject(()=>FirmwareEngine.Run(image,Identity(),fake,s=>{}),"identity changed after confirmation");Assert(fake.Calls.Count==1,"identity failure before reboot");
        fake=new Fake{BadFinal=true};Reject(()=>FirmwareEngine.Run(image,Identity(),fake,s=>{}),"post-update mismatch is not success");
        fake=new Fake();Reject(()=>FirmwareEngine.Run(image,Identity(),fake,s=>{throw new IOException("log failure");}),"log failure surfaced");Assert(fake.Calls.Count==1,"log failure before reboot stops write");
        using(var dialog=new FirmwareDialog(new FirmwarePlan{Device=new Device{Model="R3HB11"},Identity=Identity(),Image=image,FileName="offline-test.bin"})) {
            Assert(dialog.CancelButton!=null && !dialog.Attempted && !dialog.Completed,"firmware dialog construction never starts update");
            Assert(dialog.Controls.OfType<System.Windows.Forms.TextBox>().Single().ReadOnly,"firmware summary accessible read-only text");
        }
    }
}
}
