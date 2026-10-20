using System.Reflection;

[assembly: AssemblyTitle("REALFORCE 无障碍助手")]
[assembly: AssemblyDescription("适用于 REALFORCE R3HB 系列 的独立无障碍设置助手")]
[assembly: AssemblyCompany("yxycode17")]
[assembly: AssemblyProduct("REALFORCE 无障碍助手")]
[assembly: AssemblyCopyright("助手程序开发者：yxycode17")]
[assembly: AssemblyVersion(RealforceAccessible.AppInfo.AssemblyVersion)]
[assembly: AssemblyFileVersion(RealforceAccessible.AppInfo.AssemblyVersion)]
[assembly: AssemblyInformationalVersion(RealforceAccessible.AppInfo.Version)]

namespace RealforceAccessible {
    static class AppInfo {
        public const string Version="0.2.3";
        public const string AssemblyVersion=Version+".0";
        public const string Developer="yxycode17";
        public const string Title="REALFORCE 无障碍助手 "+Version;
        public const string About="REALFORCE 无障碍助手\r\n版本："+Version+"\r\n开发者："+Developer+
            "\r\n\r\n平台：AnyCPU（64 位 Windows 上以 64 位运行）。\r\n运行环境：Windows、.NET Framework 4.8。\r\n适配：R3HB 系列（仅 HB11 已实机验证），REALFORCE 通信库 4.1.1.0。\r\n独立开发的辅助工具，非 Topre 官方软件。\r\n仅加载程序同目录的通信库和布局文件，无需安装原软件。";
    }
}
