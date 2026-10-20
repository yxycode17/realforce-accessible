using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace RealforceAccessible {
static class ConfigurationFile {
    // Only user-editable settings are portable. Reserved bytes, pairing and
    // device counters always remain from the newly read destination snapshot.
    static Dictionary<string,string> Values(Snapshot s) {
        var v=new Dictionary<string,string>();
        v.Add("name",Snapshot.DeviceName(s.Edit[40]));
        v.Add("apc",s.Byte(48,0).ToString());v.Add("eco",s.Byte(52,0).ToString());
        if(s.CustomEco)for(int i=0;i<3;i++)v.Add("ecoCustom."+i,s.Byte(54,i).ToString());
        foreach(var k in s.Keys) {
            for(int layer=0;layer<4;layer++)v.Add("key."+k.Index+".layer."+layer,s.Mapping(layer,k.Index).ToString());
            for(int apc=0;apc<2;apc++)v.Add("key."+k.Index+".apc."+apc,s.Byte(56+apc*4,k.Index).ToString());
        }
        for(int i=0;i<s.ShortcutCount*2;i++)v.Add("shortcut."+i,s.Byte(45,i).ToString());
        return v;
    }
    public static string Encode(Snapshot s,bool indented=true) {
        if(s.NeedsRefresh)throw new IOException("上次保存失败，请刷新后再导出配置。");
        var doc=new XmlDocument();var root=doc.CreateElement("root");root.SetAttribute("type","object");doc.AppendChild(root);
        Add(root,"format","string","RealforceAccessibleConfiguration");Add(root,"formatVersion","number","1");
        Add(root,"model","string",s.Device.Model);Add(root,"firmware","string",s.Firmware);Add(root,"appVersion","string",AppInfo.Version);
        var settings=Add(root,"settings","object",null);
        foreach(var p in Values(s))Add(settings,p.Key,p.Key=="name"?"string":"number",p.Value);
        using(var stream=new MemoryStream()) {
            using(var writer=JsonReaderWriterFactory.CreateJsonWriter(stream,Encoding.UTF8,false,indented,"  "))doc.WriteTo(writer);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
    static XmlElement Add(XmlElement parent,string name,string type,string value) {
        var e=parent.OwnerDocument.CreateElement(name);e.SetAttribute("type",type);if(value!=null)e.InnerText=value;parent.AppendChild(e);return e;
    }
    static Dictionary<string,XmlElement> Members(XmlElement parent) {
        if(parent.GetAttribute("type")!="object")throw new IOException("预期 JSON 对象。");
        var result=new Dictionary<string,XmlElement>();
        foreach(XmlNode node in parent.ChildNodes){var e=node as XmlElement;if(e==null || result.ContainsKey(e.Name))throw new IOException("JSON 字段重复或无效。");result.Add(e.Name,e);}
        return result;
    }
    static string Scalar(XmlElement e,string type) {
        if(e.GetAttribute("type")!=type || e.ChildNodes.OfType<XmlElement>().Any())throw new IOException("字段类型错误："+e.Name);
        if(type=="number" && !Regex.IsMatch(e.InnerText,@"\A(?:0|[1-9][0-9]*)\z"))throw new IOException("字段必须为非负整数："+e.Name);
        return e.InnerText;
    }
    static int Number(Dictionary<string,string> v,string id,int max) {
        int n;if(!int.TryParse(v[id],out n) || n<0 || n>max)throw new IOException("配置值无效："+id);return n;
    }
    public static Dictionary<int,byte[]> Decode(string text,Snapshot target,List<string> ignored=null) {
        if(target.NeedsRefresh)throw new IOException("上次保存失败，请刷新设备后再导入。");
        if(text.Length>1024*1024)throw new IOException("配置文件过大。");
        var doc=new XmlDocument{XmlResolver=null};
        using(var reader=JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(text),new XmlDictionaryReaderQuotas{MaxDepth=8,MaxStringContentLength=1024*1024,MaxArrayLength=1024*1024,MaxNameTableCharCount=65536}))doc.Load(reader);
        var root=Members(doc.DocumentElement);
        var rootFields=new[]{"format","formatVersion","model","firmware","appVersion","settings"};
        if(rootFields.Except(root.Keys).Any())throw new IOException("配置文件顶层必要字段缺失。");
        if(ignored!=null)ignored.AddRange(root.Keys.Except(rootFields));
        if(Scalar(root["format"],"string")!="RealforceAccessibleConfiguration" || Scalar(root["formatVersion"],"number")!="1")throw new IOException("不支持的配置文件格式版本。");
        if(!Device.IsSupported(Scalar(root["model"],"string")) || root["model"].InnerText!=target.Device.Model)throw new IOException("配置文件与当前键盘型号不匹配。");
        if(Scalar(root["firmware"],"string")!=target.Firmware)throw new IOException("配置文件与当前键盘固件版本不匹配，已取消导入。");
        Version version;if(!Version.TryParse(Scalar(root["appVersion"],"string"),out version))throw new IOException("助手版本字段无效。");
        var expected=Values(target);var v=new Dictionary<string,string>();
        foreach(var p in Members(root["settings"])) {
            if(!expected.ContainsKey(p.Key)){if(ignored!=null)ignored.Add("settings."+p.Key);continue;}
            v.Add(p.Key,Scalar(p.Value,p.Key=="name"?"string":"number"));
        }
        if(v.Count!=expected.Count)throw new IOException("配置项目不完整，或快捷键数量、键盘布局不匹配。");
        // Work on a copy. A rejected file must never partially change the editor.
        var copy=new Snapshot{Original=target.Original,Keys=target.Keys,Edit=target.Edit.ToDictionary(p=>p.Key,p=>(byte[])p.Value.Clone())};
        copy.SetName(v["name"]);
        copy.SetByte(48,0,Number(v,"apc",5));copy.SetByte(52,0,Number(v,"eco",4));
        if(target.CustomEco) {
            int duration=Number(v,"ecoCustom.0",180);
            if(duration!=0 && (duration<10 || duration%10!=0))throw new IOException("ECO 闲置时间无效。");
            copy.SetByte(54,0,duration);copy.SetByte(54,1,Number(v,"ecoCustom.1",1));copy.SetByte(54,2,Number(v,"ecoCustom.2",2));
        }
        foreach(var k in target.Keys) {
            for(int layer=0;layer<4;layer++) {
                int code=Number(v,"key."+k.Index+".layer."+layer,65535);
                if(layer%2==1 && copy.Mapping(layer-1,k.Index)==4096) {
                    int original=target.OriginalMapping(layer,k.Index);
                    if(code!=original && ignored!=null)ignored.Add("settings.key."+k.Index+".layer."+layer+"（普通层为 Fn，该层锁定）");
                    copy.SetMapping(layer,k.Index,original);continue;
                }
                bool supported=Names.CanMap(code,target.ShortcutCount);
                if(!supported && code!=target.Mapping(layer,k.Index))throw new IOException("导入包含助手尚不支持的新映射："+code+"。只能保留该键当前已有的特殊映射。");
                copy.SetMapping(layer,k.Index,code);
            }
            for(int apc=0;apc<2;apc++)copy.SetByte(56+apc*4,k.Index,Number(v,"key."+k.Index+".apc."+apc,3));
        }
        for(int i=0;i<target.ShortcutCount*2;i++)copy.SetByte(45,i,Number(v,"shortcut."+i,i%2==0?255:223));
        copy.ValidateFnLocks();
        return copy.Edit;
    }
    public static Dictionary<int,byte[]> Read(string path,Snapshot target,List<string> ignored=null) {
        if(new FileInfo(path).Length>1024*1024)throw new IOException("配置文件过大。");
        return Decode(File.ReadAllText(path,Encoding.UTF8),target,ignored);
    }
}
}
