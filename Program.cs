using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace FlashToolbox
{
    class Program
    {
        // ADB/Fastboot 路径，支持自定义
        private static string adbPath = "adb.exe";
        private static string fastbootPath = "fastboot.exe";
        private static readonly string logFolder = "logs";
        private const string PASSWORD = "bzdabox123";
        private const string VERSION = "V2.0";
        private static string payloadDumperPath = "payload-dumper-go.exe";
        private static string scrcpyPath = "scrcpy.exe";
        private static readonly string configFile = "config.json";
        private static readonly string toolsFolder = "拓展";

        // 内测模式相关
        private static bool isBetaMode = false;
        private static string betaPassword = "neice123456";
        private static Dictionary<string, string> virtualDevice = new Dictionary<string, string>();

        // 备份相关变量
        private static string backupPath = "";
        private static bool hasBackup = false;
        private static Dictionary<string, string> backupIndex = new Dictionary<string, string>();

        // 颜色定义
        private static readonly ConsoleColor TitleColor = ConsoleColor.Cyan;
        private static readonly ConsoleColor AccentColor = ConsoleColor.Magenta;
        private static readonly ConsoleColor SuccessColor = ConsoleColor.Green;
        private static readonly ConsoleColor WarningColor = ConsoleColor.Yellow;
        private static readonly ConsoleColor ErrorColor = ConsoleColor.Red;
        private static readonly ConsoleColor InfoColor = ConsoleColor.White;
        private static readonly ConsoleColor MenuColor = ConsoleColor.Green;
        private static readonly ConsoleColor BetaColor = ConsoleColor.DarkYellow;

        // 基带相关分区列表
        private static readonly HashSet<string> BasebandPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "modem", "bluetooth", "dsp", "tz", "hyp", "aop", "abl", "xbl", "xbl_config",
            "keymaster", "devcfg", "cpucp", "qupfw", "shrm", "uefisecapp", "qweslicstore",
            "imagefv", "featenabler", "logdump", "storsec", "limits",
            "spunvm", "uefivarstore", "catecontentfv", "catefv", "hw", "rawdump"
        };

        // Windows API 声明
        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr ShellExecute(IntPtr hwnd, string lpOperation, string lpFile, string lpParameters, string lpDirectory, int nShowCmd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

        [DllImport("user32.dll")]
        private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
        private const uint INPUTLANGCHANGE_SYSCHARSET = 0x0001;
        private const int GWL_STYLE = -16;
        private const uint ES_QUICKEDIT = 0x0040;
        private const int SW_SHOWMAXIMIZED = 3;

        // 设备模式枚举
        enum DeviceMode
        {
            Unknown,
            System,
            Fastboot,
            FastbootD,
            Recovery
        }

        static void Main(string[] args)
        {
            // 检查并请求管理员权限
            if (!IsAdministrator())
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (!string.IsNullOrEmpty(exePath))
                {
                    ShellExecute(IntPtr.Zero, "runas", exePath, string.Join(" ", args), null, 1);
                }
                return;
            }

            // 获取控制台窗口句柄并最大化
            IntPtr consoleHandle = GetConsoleWindow();
            if (consoleHandle != IntPtr.Zero)
            {
                ShowWindow(consoleHandle, SW_SHOWMAXIMIZED);
            }

            // 设置控制台窗口大小和缓冲区
            if (OperatingSystem.IsWindows())
            {
                Console.SetWindowSize(120, 40);
                Console.SetBufferSize(120, 500);
            }

            // 切换输入法为英文小写
            SwitchToEnglishInput();

            // 检查并设置工具路径
            CheckAndSetToolPaths();

            // 加载配置
            LoadConfig();

            // 密码验证（带掩码，期间禁用粘贴）
            ShowPasswordPrompt();

            // 临时禁用粘贴
            bool originalState = DisableConsolePaste(true);
            string inputPwd = ReadPassword();
            // 恢复粘贴
            DisableConsolePaste(originalState);

            if (inputPwd == betaPassword)
            {
                isBetaMode = true;
                Console.ForegroundColor = BetaColor;
                Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║                  内测模式已激活！                          ║");
                Console.WriteLine("║              所有功能将使用虚拟设备进行测试                ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
                Console.ResetColor();
                InitializeVirtualDevice();
                Thread.Sleep(1500);
            }
            else if (inputPwd != PASSWORD)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n密码错误，程序退出。");
                Console.ResetColor();
                Console.ReadKey();
                return;
            }
            else
            {
                Console.ForegroundColor = SuccessColor;
                Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║                    密码验证成功！                          ║");
                Console.WriteLine("║                    欢迎使用 ColorOS 刷机工具箱            ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
                Console.ResetColor();
                Thread.Sleep(1500);
            }

            Console.Title = $"ColorOS box {VERSION}{(isBetaMode ? " - 内测模式" : "")}";

            if (!Directory.Exists(logFolder))
                Directory.CreateDirectory(logFolder);

            if (!CheckTools() && !isBetaMode)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("错误：未找到 ADB 或 Fastboot 工具。");
                Console.ResetColor();
                Console.WriteLine("请将 platform-tools 目录添加到系统 PATH，或创建'拓展'文件夹并放入工具。");
                Console.Write("是否现在手动指定路径？(y/n): ");
                if (Console.ReadLine()?.Trim().ToLower() == "y")
                {
                    ConfigureToolPaths();
                    if (!CheckTools())
                    {
                        Console.WriteLine("仍然无法找到工具，程序退出。");
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            if (!isBetaMode)
                ShowDeviceInfoOnStart();
            else
                ShowVirtualDeviceInfo();

            while (true)
            {
                ShowMainMenu();
                string choice = Console.ReadLine()?.Trim().ToUpper();
                bool skipWait = false;

                switch (choice)
                {
                    case "1": NormalFlash(); break;
                    case "2": FixFastbootState(); break;
                    case "3": ForceFlashRepairSuper(); break;
                    case "4": ExtractAndFlashPayload(); break;
                    case "5": FastbootDAutoFlash(); break;
                    case "6": AdbScreenMirror(); break;
                    case "7": AdbSideload(); break;
                    case "8": OpenCMD(); break;
                    case "9": GlobalSettings(); break;
                    case "10": ShowHelp(); break;
                    case "11": UnlockBootloaderMenu(); break;
                    case "12": OpenLogFolder(); break;
                    case "14": BackupData(); break;
                    case "15": RestoreData(); break;
                    case "B": FlashBasebandOnly(); break;
                    case "T": BootToRecoveryTest(); break;
                    case "D": GetDeviceInfo(); Console.WriteLine("\n按任意键返回主菜单..."); Console.ReadKey(); skipWait = true; break;
                    case "Q": Console.WriteLine("退出工具箱..."); return;
                    default: Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("无效选项，请重新输入。"); Console.ResetColor(); break;
                }

                if (!skipWait)
                {
                    Console.WriteLine("\n按任意键继续...");
                    Console.ReadKey();
                }
            }
        }

        // ========== 设备状态检测 ==========
        static DeviceMode GetDeviceMode()
        {
            if (isBetaMode) return DeviceMode.System;

            // 先检查 ADB 设备
            string adbDevices = GetAdbOutput("devices");
            if (!string.IsNullOrEmpty(adbDevices) && adbDevices.Contains("device") && !adbDevices.Contains("offline"))
            {
                // 检查是否是 FastbootD 模式（通过 getvar 命令）
                string fbVersion = GetAdbOutput("shell getprop ro.boot.fastboot");
                if (!string.IsNullOrEmpty(fbVersion) && fbVersion.Contains("1"))
                    return DeviceMode.FastbootD;
                return DeviceMode.System;
            }

            // 检查 Fastboot 设备
            string fastbootDevices = RunFastbootCommand("devices");
            if (!string.IsNullOrEmpty(fastbootDevices) && fastbootDevices.Contains("fastboot"))
                return DeviceMode.Fastboot;

            return DeviceMode.Unknown;
        }

        // 等待设备进入指定模式
        static bool WaitForDeviceMode(DeviceMode targetMode, int timeoutSeconds = 30)
        {
            PrintInfo($"等待设备进入 {targetMode} 模式...");
            DateTime startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
            {
                DeviceMode currentMode = GetDeviceMode();
                if (currentMode == targetMode)
                {
                    PrintSuccess($"设备已进入 {targetMode} 模式");
                    Thread.Sleep(2000);
                    return true;
                }
                PrintInfo($"当前模式: {currentMode}，等待中...");
                Thread.Sleep(2000);
            }

            PrintError($"等待设备进入 {targetMode} 模式超时");
            return false;
        }

        // 自动进入 FastbootD 模式
        static bool EnterFastbootD()
        {
            DeviceMode currentMode = GetDeviceMode();

            if (currentMode == DeviceMode.FastbootD)
            {
                PrintSuccess("设备已在 FastbootD 模式");
                return true;
            }

            if (currentMode == DeviceMode.Fastboot)
            {
                PrintInfo("设备在 Fastboot 模式，正在切换到 FastbootD...");
                RunCommand(fastbootPath, "reboot fastboot");
                Thread.Sleep(5000);
                return WaitForDeviceMode(DeviceMode.FastbootD);
            }

            if (currentMode == DeviceMode.System)
            {
                PrintInfo("设备在系统模式，正在重启到 FastbootD...");
                RunCommand(adbPath, "reboot fastboot");
                Thread.Sleep(5000);
                return WaitForDeviceMode(DeviceMode.FastbootD);
            }

            PrintError("无法进入 FastbootD 模式");
            return false;
        }

        // 自动进入 Fastboot 模式
        static bool EnterFastboot()
        {
            DeviceMode currentMode = GetDeviceMode();

            if (currentMode == DeviceMode.Fastboot)
            {
                PrintSuccess("设备已在 Fastboot 模式");
                return true;
            }

            if (currentMode == DeviceMode.FastbootD)
            {
                PrintInfo("设备在 FastbootD 模式，正在切换到 Fastboot...");
                RunCommand(fastbootPath, "reboot bootloader");
                Thread.Sleep(5000);
                return WaitForDeviceMode(DeviceMode.Fastboot);
            }

            if (currentMode == DeviceMode.System)
            {
                PrintInfo("设备在系统模式，正在重启到 Fastboot...");
                RunCommand(adbPath, "reboot bootloader");
                Thread.Sleep(5000);
                return WaitForDeviceMode(DeviceMode.Fastboot);
            }

            PrintError("无法进入 Fastboot 模式");
            return false;
        }

        // 智能重启设备
        static void SmartReboot()
        {
            DeviceMode currentMode = GetDeviceMode();
            PrintInfo($"当前设备模式: {currentMode}，正在重启...");

            switch (currentMode)
            {
                case DeviceMode.System:
                    RunCommand(adbPath, "reboot");
                    break;
                case DeviceMode.Fastboot:
                case DeviceMode.FastbootD:
                    RunCommand(fastbootPath, "reboot");
                    break;
                default:
                    PrintWarning("无法识别设备模式，尝试使用 ADB 重启...");
                    RunCommand(adbPath, "reboot");
                    break;
            }

            Thread.Sleep(3000);
            PrintSuccess("设备正在重启...");
        }

        // 检查并确保 Bootloader 已解锁
        static bool EnsureBootloaderUnlocked()
        {
            if (!EnterFastboot())
                return false;

            if (!CheckBootloaderUnlockedInFastboot())
            {
                PrintError("Bootloader 未解锁！无法进行刷机。");
                PrintInfo("请先使用选项11解锁 Bootloader。");
                return false;
            }

            PrintSuccess("Bootloader 已解锁");
            return true;
        }

        // 检查并设置工具路径
        static void CheckAndSetToolPaths()
        {
            if (Directory.Exists(toolsFolder))
            {
                PrintInfo($"检测到工具文件夹: {toolsFolder}");

                var toolsToFind = new Dictionary<string, Action<string>>
                {
                    { "adb.exe", (path) => { adbPath = path; PrintSuccess($"已找到 ADB: {path}"); } },
                    { "fastboot.exe", (path) => { fastbootPath = path; PrintSuccess($"已找到 Fastboot: {path}"); } },
                    { "payload-dumper-go.exe", (path) => { payloadDumperPath = path; PrintSuccess($"已找到 payload-dumper-go: {path}"); } },
                    { "scrcpy.exe", (path) => { scrcpyPath = path; PrintSuccess($"已找到 scrcpy: {path}"); } }
                };

                foreach (var tool in toolsToFind)
                {
                    string toolPath = Path.Combine(toolsFolder, tool.Key);
                    if (File.Exists(toolPath))
                        tool.Value(toolPath);
                }

                try
                {
                    string[] subDirs = Directory.GetDirectories(toolsFolder);
                    foreach (string subDir in subDirs)
                    {
                        foreach (var tool in toolsToFind)
                        {
                            string toolPath = Path.Combine(subDir, tool.Key);
                            if (File.Exists(toolPath))
                            {
                                if (tool.Key == "adb.exe" && adbPath == "adb.exe")
                                    tool.Value(toolPath);
                                else if (tool.Key == "fastboot.exe" && fastbootPath == "fastboot.exe")
                                    tool.Value(toolPath);
                                else if (tool.Key == "payload-dumper-go.exe" && payloadDumperPath == "payload-dumper-go.exe")
                                    tool.Value(toolPath);
                                else if (tool.Key == "scrcpy.exe" && scrcpyPath == "scrcpy.exe")
                                    tool.Value(toolPath);
                            }
                        }
                    }
                }
                catch { }

                if (payloadDumperPath == "payload-dumper-go.exe")
                {
                    string[] payloadNames = { "payload-dumper-go.exe", "payload-dumper-go_1.2.2_windows_amd64.exe" };

                    foreach (var name in payloadNames)
                    {
                        string toolPath = Path.Combine(toolsFolder, name);
                        if (File.Exists(toolPath))
                        {
                            payloadDumperPath = toolPath;
                            PrintSuccess($"已找到 payload-dumper-go: {toolPath}");
                            break;
                        }
                    }

                    if (payloadDumperPath == "payload-dumper-go.exe")
                    {
                        try
                        {
                            string[] subDirs = Directory.GetDirectories(toolsFolder);
                            foreach (string subDir in subDirs)
                            {
                                foreach (var name in payloadNames)
                                {
                                    string toolPath = Path.Combine(subDir, name);
                                    if (File.Exists(toolPath))
                                    {
                                        payloadDumperPath = toolPath;
                                        PrintSuccess($"已找到 payload-dumper-go: {toolPath}");
                                        break;
                                    }
                                }
                                if (payloadDumperPath != "payload-dumper-go.exe") break;
                            }
                        }
                        catch { }
                    }
                }

                if (scrcpyPath == "scrcpy.exe")
                {
                    string[] scrcpyDirs = { "scrcpy-win64-v2.4", "scrcpy-win64-v2.3", "scrcpy" };

                    foreach (var dir in scrcpyDirs)
                    {
                        string toolPath = Path.Combine(toolsFolder, dir, "scrcpy.exe");
                        if (File.Exists(toolPath))
                        {
                            scrcpyPath = toolPath;
                            PrintSuccess($"已找到 scrcpy: {toolPath}");
                            break;
                        }
                    }
                }

                Console.WriteLine();
            }
        }

        static void InitializeVirtualDevice()
        {
            virtualDevice["model"] = "OnePlus 9RT (测试设备)";
            virtualDevice["brand"] = "OnePlus";
            virtualDevice["manufacturer"] = "OnePlus";
            virtualDevice["device"] = "OP5169L1";
            virtualDevice["android_version"] = "13";
            virtualDevice["sdk"] = "33";
            virtualDevice["security_patch"] = "2024-12-05";
            virtualDevice["build_id"] = "TP1A.220624.014";
            virtualDevice["cpu_abi"] = "arm64-v8a";
            virtualDevice["resolution"] = "1080x2400";
            virtualDevice["density"] = "420";
            virtualDevice["bootloader"] = "unlocked";
            virtualDevice["root"] = "已 Root";
            virtualDevice["battery_level"] = "87";
            virtualDevice["battery_status"] = "充电中";
            virtualDevice["total_memory"] = "12GB";
            virtualDevice["available_memory"] = "5.2GB";
            virtualDevice["storage_total"] = "256GB";
            virtualDevice["storage_free"] = "128GB";

            PrintSuccess("虚拟设备已初始化，所有功能将模拟真实设备行为。");
        }

        static void ShowVirtualDeviceInfo()
        {
            SetColor(BetaColor);
            Console.WriteLine("\n[虚拟设备信息 - 内测模式]");
            ResetColor();

            Console.WriteLine($"设备型号      : {virtualDevice["model"]}");
            Console.WriteLine($"品牌          : {virtualDevice["brand"]}");
            Console.WriteLine($"制造商        : {virtualDevice["manufacturer"]}");
            Console.WriteLine($"设备代号      : {virtualDevice["device"]}");
            Console.WriteLine($"Android 版本  : {virtualDevice["android_version"]} (SDK {virtualDevice["sdk"]})");
            Console.WriteLine($"Bootloader 状态: {virtualDevice["bootloader"]}");
            Console.WriteLine($"Root 状态      : {virtualDevice["root"]}");
            Console.WriteLine($"电量          : {virtualDevice["battery_level"]}% ({virtualDevice["battery_status"]})");
            Console.WriteLine();
        }

        static string GetVirtualAdbOutput(string arguments)
        {
            if (arguments.Contains("devices"))
                return "List of devices attached\n1234567890ABCDEF\tdevice\n";
            if (arguments.Contains("shell getprop ro.product.model"))
                return virtualDevice["model"];
            if (arguments.Contains("shell getprop ro.product.brand"))
                return virtualDevice["brand"];
            if (arguments.Contains("shell getprop ro.product.manufacturer"))
                return virtualDevice["manufacturer"];
            if (arguments.Contains("shell getprop ro.product.device"))
                return virtualDevice["device"];
            if (arguments.Contains("shell getprop ro.build.version.release"))
                return virtualDevice["android_version"];
            if (arguments.Contains("shell getprop ro.build.version.sdk"))
                return virtualDevice["sdk"];
            if (arguments.Contains("shell getprop ro.build.version.security_patch"))
                return virtualDevice["security_patch"];
            if (arguments.Contains("shell getprop ro.build.id"))
                return virtualDevice["build_id"];
            if (arguments.Contains("shell getprop ro.product.cpu.abi"))
                return virtualDevice["cpu_abi"];
            if (arguments.Contains("shell wm size"))
                return $"Physical size: {virtualDevice["resolution"]}";
            if (arguments.Contains("shell wm density"))
                return $"Physical density: {virtualDevice["density"]}";
            if (arguments.Contains("shell getprop ro.boot.flash.locked"))
                return virtualDevice["bootloader"] == "unlocked" ? "0" : "1";
            if (arguments.Contains("shell su -c 'echo root'"))
                return virtualDevice["root"] == "已 Root" ? "root" : "";
            if (arguments.Contains("shell dumpsys battery"))
                return $"  level: {virtualDevice["battery_level"]}\n  status: {(virtualDevice["battery_status"] == "充电中" ? "2" : "1")}";
            if (arguments.Contains("shell cat /proc/meminfo"))
                return $"MemTotal:        {int.Parse(virtualDevice["total_memory"]) * 1024 * 1024} kB\nMemAvailable:    {int.Parse(virtualDevice["available_memory"]) * 1024 * 1024} kB";
            if (arguments.Contains("shell df -h /data"))
                return $"Filesystem      Size  Used Avail Use% Mounted on\n/dev/block/sda13  {virtualDevice["storage_total"]}  128G  128G  50% /data";
            if (arguments.Contains("shell pm list packages -3"))
                return "package:com.test.app1\npackage:com.test.app2\npackage:com.test.app3";
            if (arguments.Contains("shell pm path"))
            {
                string pkg = arguments.Split(' ')[^1];
                return $"package:/data/app/{pkg}-xxx/base.apk";
            }
            if (arguments.Contains("shell content query --uri content://contacts/people"))
                return "Row: 1, name=测试用户, phone=13800138000";
            if (arguments.Contains("shell content query --uri content://sms"))
                return "Row: 1, address=10086, body=测试短信, date=1234567890";
            if (arguments.Contains("shell content query --uri content://call_log/calls"))
                return "Row: 1, number=13800138000, type=1, duration=120";
            if (arguments.Contains("shell ls -la /sdcard/"))
                return "drwxrwx--x root     sdcard_rw          2024-01-01 00:00 Download\ndrwxrwx--x root     sdcard_rw          2024-01-01 00:00 DCIM";
            return "";
        }

        static string TrimPathQuotes(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.Trim('"', '\'');
        }

        static void EnableConsolePaste()
        {
            try
            {
                IntPtr consoleHandle = GetConsoleWindow();
                if (consoleHandle != IntPtr.Zero)
                {
                    uint style = GetWindowLong(consoleHandle, GWL_STYLE);
                    style |= ES_QUICKEDIT;
                    SetWindowLong(consoleHandle, GWL_STYLE, style);
                }
            }
            catch { }
        }

        static void BootToRecoveryTest()
        {
            PrintHeader("临时启动 Recovery - 测试功能！");
            PrintWarning("此功能适用于 A/B/VAB 设备（无独立 Recovery 分区）");
            PrintInfo("通过 fastboot 命令临时启动 recovery 镜像，不会刷写设备\n");

            if (!isBetaMode)
            {
                if (!EnterFastboot())
                    return;
            }
            else
            {
                PrintInfo("[内测模式] 使用虚拟设备测试...");
            }

            Console.Write("请输入 recovery.img 镜像文件完整路径: ");
            string recoveryPath = GetFilePathInput("请输入 recovery.img 镜像文件完整路径: ");
            recoveryPath = TrimPathQuotes(recoveryPath);

            if (string.IsNullOrEmpty(recoveryPath) || (!isBetaMode && !File.Exists(recoveryPath)))
            {
                PrintError("文件不存在，操作取消。");
                return;
            }

            PrintInfo($"正在临时启动 recovery 镜像: {Path.GetFileName(recoveryPath)}");
            PrintWarning("注意：这只是临时启动，不会刷写设备！");

            if (!isBetaMode)
                RunCommand(fastbootPath, $"boot \"{recoveryPath}\"");
            else
                PrintSuccess("[内测模式] 模拟临时启动 Recovery 成功！");

            PrintSuccess("Recovery 镜像已临时启动！");
        }

        static void SwitchToEnglishInput()
        {
            try
            {
                IntPtr hWnd = GetForegroundWindow();
                if (hWnd != IntPtr.Zero)
                {
                    PostMessage(hWnd, WM_INPUTLANGCHANGEREQUEST, (IntPtr)INPUTLANGCHANGE_SYSCHARSET, (IntPtr)0x0409);
                }
            }
            catch { }
        }

        static bool DisableConsolePaste(bool disable)
        {
            try
            {
                IntPtr consoleHandle = GetConsoleWindow();
                if (consoleHandle != IntPtr.Zero)
                {
                    uint style = GetWindowLong(consoleHandle, GWL_STYLE);
                    bool wasDisabled = (style & ES_QUICKEDIT) == 0;
                    if (disable)
                    {
                        style &= ~ES_QUICKEDIT;
                    }
                    else
                    {
                        style |= ES_QUICKEDIT;
                    }
                    SetWindowLong(consoleHandle, GWL_STYLE, style);
                    return wasDisabled;
                }
            }
            catch { }
            return false;
        }

        static bool IsAdministrator()
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }

        static void ShowPasswordPrompt()
        {
            Console.Clear();
            SetColor(AccentColor);
            Console.WriteLine("================================================================================");
            SetColor(TitleColor);
            Console.WriteLine("     ██████╗ ██████╗ ██╗      ██████╗ ██████╗ ██████╗ ███████╗    ");
            Console.WriteLine("    ██╔════╝██╔═══██╗██║     ██╔═══██╗██╔══██╗██╔══██╗██╔════╝    ");
            Console.WriteLine("    ██║     ██║   ██║██║     ██║   ██║██████╔╝██║  ██║███████╗    ");
            Console.WriteLine("    ██║     ██║   ██║██║     ██║   ██║██╔══██╗██║  ██║╚════██║    ");
            Console.WriteLine("    ╚██████╗╚██████╔╝███████╗╚██████╔╝██║  ██║██████╔╝███████║    ");
            Console.WriteLine("     ╚═════╝ ╚═════╝ ╚══════╝ ╚═════╝ ╚═╝  ╚═╝╚═════╝ ╚══════╝    ");
            SetColor(SuccessColor);
            Console.WriteLine($"                             版本 {VERSION}                                    ");
            SetColor(AccentColor);
            Console.WriteLine("================================================================================");
            SetColor(WarningColor);
            Console.WriteLine("  作者：酷安 不知道是不知道啊              交流群：478538539");
            Console.WriteLine("  禁止盗用，违者必究");
            SetColor(AccentColor);
            Console.WriteLine("================================================================================");
            ResetColor();
            Console.WriteLine();
            SetColor(InfoColor);
            Console.Write("请输入启动密码: ");
            ResetColor();
        }

        static void SetColor(ConsoleColor color) { Console.ForegroundColor = color; }
        static void ResetColor() { Console.ResetColor(); }
        static void PrintHeader(string text) { SetColor(AccentColor); Console.WriteLine($"\n================================================================================"); Console.WriteLine($"  {text}"); Console.WriteLine($"================================================================================"); ResetColor(); }
        static void PrintSuccess(string message) { SetColor(SuccessColor); Console.WriteLine($"[成功] {message}"); ResetColor(); }
        static void PrintError(string message) { SetColor(ErrorColor); Console.WriteLine($"[错误] {message}"); ResetColor(); }
        static void PrintWarning(string message) { SetColor(WarningColor); Console.WriteLine($"[警告] {message}"); ResetColor(); }
        static void PrintInfo(string message) { SetColor(InfoColor); Console.WriteLine($"[信息] {message}"); ResetColor(); }

        class ProgressBar
        {
            private readonly int total;
            private int current;
            private readonly int barWidth = 50;
            private readonly string operationName;
            private readonly ConsoleColor barColor;

            public ProgressBar(int totalSteps, string operation, ConsoleColor color = ConsoleColor.Green)
            {
                total = totalSteps;
                current = 0;
                operationName = operation;
                barColor = color;
                Draw();
            }

            public void Update(int step)
            {
                current = step;
                Draw();
            }

            public void Increment()
            {
                current++;
                Draw();
            }

            private void Draw()
            {
                int percent = (int)((double)current / total * 100);
                int filled = (int)((double)current / total * barWidth);

                Console.ForegroundColor = barColor;
                string bar = new string('=', filled) + new string('-', barWidth - filled);
                Console.Write($"\r{operationName}: [{bar}] {percent:D3}% ({current}/{total})");
                Console.ResetColor();

                if (current == total)
                {
                    Console.ForegroundColor = SuccessColor;
                    Console.Write(" [完成]");
                    Console.ResetColor();
                    Console.WriteLine();
                }
            }
        }

        static void LoadConfig()
        {
            if (File.Exists(configFile))
            {
                try
                {
                    string json = File.ReadAllText(configFile);
                    var config = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (config != null)
                    {
                        if (config.ContainsKey("payloadDumperPath")) payloadDumperPath = config["payloadDumperPath"];
                        if (config.ContainsKey("scrcpyPath")) scrcpyPath = config["scrcpyPath"];
                    }
                }
                catch { }
            }
        }

        static void SaveConfig()
        {
            var config = new Dictionary<string, string> { { "payloadDumperPath", payloadDumperPath }, { "scrcpyPath", scrcpyPath } };
            string json = JsonSerializer.Serialize(config);
            File.WriteAllText(configFile, json);
        }

        static string ReadPassword()
        {
            string password = "";
            ConsoleKeyInfo key;
            do
            {
                key = Console.ReadKey(true);
                if (key.Key != ConsoleKey.Backspace && key.Key != ConsoleKey.Enter)
                {
                    password += key.KeyChar;
                    Console.Write("*");
                }
                else if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                {
                    password = password.Substring(0, password.Length - 1);
                    Console.Write("\b \b");
                }
            } while (key.Key != ConsoleKey.Enter);
            return password;
        }

        static void ShowDeviceInfoOnStart()
        {
            SetColor(WarningColor);
            Console.WriteLine("\n[设备连接状态检测]");
            ResetColor();

            DeviceMode mode = GetDeviceMode();
            PrintInfo($"当前设备模式: {mode}");

            if (mode == DeviceMode.System)
            {
                string model = GetAdbOutput("shell getprop ro.product.model");
                string androidVersion = GetAdbOutput("shell getprop ro.build.version.release");
                string sdk = GetAdbOutput("shell getprop ro.build.version.sdk");
                PrintInfo($"设备型号      : {(string.IsNullOrEmpty(model) ? "未知" : model)}");
                PrintInfo($"Android 版本  : {(string.IsNullOrEmpty(androidVersion) ? "未知" : androidVersion)} (SDK {sdk})");
            }
            else if (mode == DeviceMode.Fastboot || mode == DeviceMode.FastbootD)
            {
                PrintInfo("设备处于 Fastboot/FastbootD 模式");
            }
            else
            {
                PrintWarning("未检测到设备连接");
                PrintInfo("提示：请确保设备已开启 USB 调试并连接电脑");
            }

            Console.WriteLine();
        }

        static void ShowMainMenu()
        {
            Console.Clear();
            SetColor(AccentColor);
            Console.WriteLine("================================================================================");
            SetColor(TitleColor);
            Console.WriteLine("     ██████╗ ██████╗ ██╗      ██████╗ ██████╗ ██████╗ ███████╗    ");
            Console.WriteLine("    ██╔════╝██╔═══██╗██║     ██╔═══██╗██╔══██╗██╔══██╗██╔════╝    ");
            Console.WriteLine("    ██║     ██║   ██║██║     ██║   ██║██████╔╝██║  ██║███████╗    ");
            Console.WriteLine("    ██║     ██║   ██║██║     ██║   ██║██╔══██╗██║  ██║╚════██║    ");
            Console.WriteLine("    ╚██████╗╚██████╔╝███████╗╚██████╔╝██║  ██║██████╔╝███████║    ");
            Console.WriteLine("     ╚═════╝ ╚═════╝ ╚══════╝ ╚═════╝ ╚═╝  ╚═╝╚═════╝ ╚══════╝    ");
            SetColor(SuccessColor);
            Console.WriteLine($"                             版本 {VERSION}{(isBetaMode ? " [内测模式]" : "")}                                    ");
            SetColor(AccentColor);
            Console.WriteLine("================================================================================");
            SetColor(WarningColor);
            Console.WriteLine("  作者：酷安 不知道是不知道啊              交流群：478538539");
            Console.WriteLine("  禁止盗用，违者必究");
            SetColor(AccentColor);
            Console.WriteLine("================================================================================");
            ResetColor();
            Console.WriteLine();

            SetColor(MenuColor);
            Console.WriteLine("========================== 主菜单 ==========================");
            ResetColor();
            Console.WriteLine();

            SetColor(MenuColor);
            Console.WriteLine("  [刷机相关功能]");
            ResetColor();
            Console.WriteLine("   1. 设备刷机（正常刷机流程）");
            Console.WriteLine("   2. 修复 Fastboot 状态异常");
            Console.WriteLine("   3. 强力线刷修复 Super 分区异常（强制刷入）");
            Console.WriteLine("   4. 解包 OTA 包并刷写（payload.bin）");
            Console.WriteLine("   5. 纯 FastbootD 自动刷机");
            Console.WriteLine();

            SetColor(MenuColor);
            Console.WriteLine("  [数据备份与恢复]");
            ResetColor();
            Console.WriteLine("  14. 备份手机数据");
            Console.WriteLine("  15. 恢复手机数据");
            if (hasBackup)
            {
                SetColor(SuccessColor);
                Console.WriteLine($"      ✓ 已有备份数据，共 {backupIndex.Count} 个应用");
                ResetColor();
            }
            Console.WriteLine();

            SetColor(MenuColor);
            Console.WriteLine("  [基带刷写]");
            ResetColor();
            Console.WriteLine("   B. 单独刷写基带分区（Fastboot模式）");
            Console.WriteLine();

            SetColor(MenuColor);
            Console.WriteLine("  [调试与投屏]");
            ResetColor();
            Console.WriteLine("   6. ADB 投屏（实时显示手机屏幕）");
            Console.WriteLine("   7. ADB 线刷（刷入 ZIP 包）");
            Console.WriteLine();

            SetColor(MenuColor);
            Console.WriteLine("  [测试功能]");
            ResetColor();
            Console.WriteLine("   T. 临时启动 Recovery - 测试！ (适用于 A/B/VAB 设备)");
            Console.WriteLine();

            SetColor(MenuColor);
            Console.WriteLine("  [系统工具]");
            ResetColor();
            Console.WriteLine("   8. 打开 CMD 命令栏");
            Console.WriteLine("   9. 全局设置");
            Console.WriteLine("  10. 使用说明");
            Console.WriteLine("  11. 解锁 Bootloader");
            Console.WriteLine("  12. 打开日志文件夹");
            Console.WriteLine("   D. 查看设备信息");
            Console.WriteLine("   Q. 退出");
            Console.WriteLine();

            SetColor(TitleColor);
            Console.WriteLine("============================================================");
            ResetColor();
            Console.Write("\n请选择操作: ");
        }

        static string GetFolderPathInput(string prompt)
        {
            Console.Write(prompt);
            EnableConsolePaste();
            string input = Console.ReadLine()?.Trim();
            DisableConsolePaste(true);
            return TrimPathQuotes(input);
        }

        static string GetFilePathInput(string prompt)
        {
            Console.Write(prompt);
            EnableConsolePaste();
            string input = Console.ReadLine()?.Trim();
            DisableConsolePaste(true);
            return TrimPathQuotes(input);
        }

        static string GetAdbOutput(string arguments)
        {
            if (isBetaMode)
                return GetVirtualAdbOutput(arguments);

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo { FileName = adbPath, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return !string.IsNullOrEmpty(error) ? error.Trim() : output.Trim();
                }
            }
            catch { return ""; }
        }

        static string GetPartitionFromFileName(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName).ToLower();
            var partitionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "boot", "boot" }, { "recovery", "recovery" }, { "system", "system" },
                { "system_ext", "system_ext" }, { "vendor", "vendor" }, { "vendor_boot", "vendor_boot" },
                { "product", "product" }, { "odm", "odm" }, { "userdata", "userdata" }, { "super", "super" },
                { "vbmeta", "vbmeta" }, { "vbmeta_system", "vbmeta_system" }, { "vbmeta_vendor", "vbmeta_vendor" },
                { "dtbo", "dtbo" }, { "modem", "modem" }, { "bluetooth", "bluetooth" }, { "dsp", "dsp" },
                { "tz", "tz" }, { "hyp", "hyp" }, { "aop", "aop" }, { "abl", "abl" }, { "xbl", "xbl" },
                { "xbl_config", "xbl_config" }, { "keymaster", "keymaster" }, { "devcfg", "devcfg" },
                { "cpucp", "cpucp" }, { "qupfw", "qupfw" }, { "shrm", "shrm" }, { "uefisecapp", "uefisecapp" },
                { "qweslicstore", "qweslicstore" }, { "imagefv", "imagefv" }, { "featenabler", "featenabler" },
                { "multiimgoem", "multiimgoem" }, { "oplus_sec", "oplus_sec" }, { "oplusstanvblk", "oplusstanvblk" },
                { "oplusstanvbk", "oplusstanvblk" }, { "engineering_cdt", "engineering_cdt" }, { "aol", "aol" },
                { "logdump", "logdump" }, { "storsec", "storsec" }, { "limits", "limits" }, { "spunvm", "spunvm" },
                { "uefivarstore", "uefivarstore" }, { "my_bigball", "my_bigball" }, { "my_carrier", "my_carrier" },
                { "my_company", "my_company" }, { "my_engineering", "my_engineering" }, { "my_heytap", "my_heytap" },
                { "my_manifest", "my_manifest" }, { "my_preload", "my_preload" }, { "my_product", "my_product" },
                { "my_region", "my_region" }, { "my_stock", "my_stock" }, { "splash", "splash" },
                { "persist", "persist" }, { "metadata", "metadata" }, { "misc", "misc" }
            };
            if (partitionMap.ContainsKey(name)) return partitionMap[name];
            if (name.EndsWith("_a") || name.EndsWith("_b") || name.EndsWith("_c"))
            {
                string withoutSlot = Regex.Replace(name, "_(a|b|c)$", "");
                if (partitionMap.ContainsKey(withoutSlot)) return partitionMap[withoutSlot];
            }
            if (name.Contains("boot")) return "boot";
            if (name.Contains("system_ext")) return "system_ext";
            if (name.Contains("system")) return "system";
            if (name.Contains("vendor_boot")) return "vendor_boot";
            if (name.Contains("vendor")) return "vendor";
            if (name.Contains("product")) return "product";
            if (name.Contains("odm")) return "odm";
            if (name.Contains("super")) return "super";
            if (name.Contains("vbmeta_system")) return "vbmeta_system";
            if (name.Contains("vbmeta_vendor")) return "vbmeta_vendor";
            if (name.Contains("vbmeta")) return "vbmeta";
            if (name.Contains("dtbo")) return "dtbo";
            if (name.Contains("modem")) return "modem";
            if (name.Contains("multiimgoem")) return "multiimgoem";
            if (name.Contains("oplusstanvblk") || name.Contains("oplusstanvbk")) return "oplusstanvblk";
            if (name.StartsWith("my_")) return name;
            if (name.StartsWith("oplus_")) return name;
            return null;
        }

        static bool IsBasebandPartition(string partition) => !string.IsNullOrEmpty(partition) && BasebandPartitions.Contains(partition);

        static int GetFlashMode()
        {
            Console.WriteLine("\n请选择刷写模式：");
            Console.WriteLine("  1. 刷入单分区（手动选择要刷写的镜像）");
            Console.WriteLine("  2. 全部刷入（自动识别并刷写所有镜像）");
            Console.WriteLine("  3. 不刷入（跳过当前操作）");
            Console.Write("请选择 (1/2/3): ");
            string input = Console.ReadLine()?.Trim();
            if (input == "1") return 1;
            if (input == "2") return 2;
            return 3;
        }

        static void DisplayImagesByCategory(int systemCount, int basebandCount, int unknownCount)
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                    镜像文件分类统计                        ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════╝");

            Console.WriteLine("\n┌────────────────────────────────────────────────────────────┐");
            Console.WriteLine("│  【系统分区】                                              │");
            Console.WriteLine($"│    共 {systemCount} 个                                             │");
            Console.WriteLine("├────────────────────────────────────────────────────────────┤");
            Console.WriteLine("│  【基带/固件分区】                                         │");
            Console.WriteLine($"│    共 {basebandCount} 个                                          │");
            if (unknownCount > 0)
            {
                Console.WriteLine("├────────────────────────────────────────────────────────────┤");
                Console.WriteLine("│  ⚠️ 【未识别分区】（需要手动输入分区名）                    │");
                Console.WriteLine($"│    共 {unknownCount} 个                                          │");
            }
            Console.WriteLine("└────────────────────────────────────────────────────────────┘");

            Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  总计: {systemCount + basebandCount + unknownCount} 个镜像文件                          ║");
            Console.WriteLine($"║    - 系统分区: {systemCount} 个                                    ║");
            Console.WriteLine($"║    - 基带分区: {basebandCount} 个                                    ║");
            if (unknownCount > 0) Console.WriteLine($"║    - 未识别分区: {unknownCount} 个                                    ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        }

        // ========== 备份数据功能 ==========
        static void BackupData()
        {
            PrintHeader("备份手机数据");
            PrintInfo("此功能将备份手机中的以下数据：");
            Console.WriteLine("  - 联系人 (contacts)");
            Console.WriteLine("  - 短信 (sms)");
            Console.WriteLine("  - 通话记录 (call log)");
            Console.WriteLine("  - 日历 (calendar)");
            Console.WriteLine("  - 所有用户应用 (APK 文件)");
            Console.WriteLine("  - 内部存储目录结构\n");

            if (!isBetaMode)
            {
                DeviceMode mode = GetDeviceMode();
                if (mode != DeviceMode.System)
                {
                    PrintError("备份功能需要设备处于系统模式");
                    PrintInfo("请确保设备已开机并开启 USB 调试");
                    return;
                }
            }
            else
            {
                PrintInfo("[内测模式] 使用虚拟设备进行备份测试...");
            }

            // 创建备份目录
            backupPath = Path.Combine(logFolder, $"backup_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(backupPath);

            string apkDir = Path.Combine(backupPath, "apps");
            string dataDir = Path.Combine(backupPath, "data");
            Directory.CreateDirectory(apkDir);
            Directory.CreateDirectory(dataDir);

            PrintInfo($"备份目录: {backupPath}");
            PrintInfo("开始备份数据...\n");

            // 获取所有用户应用包名
            PrintInfo("扫描用户应用...");
            string packagesOutput = GetAdbOutput("shell pm list packages -3");
            string[] packageLines = packagesOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> packageNames = new List<string>();

            foreach (string line in packageLines)
            {
                if (line.StartsWith("package:"))
                {
                    string pkg = line.Substring(8).Trim();
                    if (!string.IsNullOrEmpty(pkg) && !pkg.Contains("test"))
                        packageNames.Add(pkg);
                }
            }

            PrintInfo($"找到 {packageNames.Count} 个用户应用\n");

            var progress = new ProgressBar(packageNames.Count + 6, "备份进度");
            int step = 0;

            // 创建备份索引文件
            backupIndex.Clear();

            // 备份所有应用 APK
            PrintInfo("正在备份应用 APK 文件...");
            foreach (string pkg in packageNames)
            {
                try
                {
                    if (!isBetaMode)
                    {
                        string apkPath = GetAdbOutput($"shell pm path {pkg}").Trim();
                        if (apkPath.StartsWith("package:"))
                        {
                            apkPath = apkPath.Substring(8);
                            string outputFile = Path.Combine(apkDir, $"{pkg}.apk");
                            RunCommand(adbPath, $"pull \"{apkPath}\" \"{outputFile}\"");
                            if (File.Exists(outputFile))
                            {
                                backupIndex[pkg] = outputFile;
                                PrintInfo($"  ✓ 已备份: {pkg}");
                            }
                        }
                    }
                    else
                    {
                        string outputFile = Path.Combine(apkDir, $"{pkg}.apk");
                        File.WriteAllText(outputFile, "模拟 APK 文件内容");
                        backupIndex[pkg] = outputFile;
                        PrintInfo($"  ✓ [模拟] 已备份: {pkg}");
                    }
                }
                catch (Exception ex)
                {
                    PrintWarning($"备份 {pkg} 失败: {ex.Message}");
                }
                step++;
                progress.Update(step);
            }

            // 备份联系人
            PrintInfo("备份联系人...");
            string contactsOutput = GetAdbOutput("shell content query --uri content://contacts/people");
            File.WriteAllText(Path.Combine(dataDir, "contacts.txt"), contactsOutput);
            step++; progress.Update(step);

            // 备份短信
            PrintInfo("备份短信...");
            string smsOutput = GetAdbOutput("shell content query --uri content://sms");
            File.WriteAllText(Path.Combine(dataDir, "sms.txt"), smsOutput);
            step++; progress.Update(step);

            // 备份通话记录
            PrintInfo("备份通话记录...");
            string callLogOutput = GetAdbOutput("shell content query --uri content://call_log/calls");
            File.WriteAllText(Path.Combine(dataDir, "call_log.txt"), callLogOutput);
            step++; progress.Update(step);

            // 备份日历
            PrintInfo("备份日历...");
            string calendarOutput = GetAdbOutput("shell content query --uri content://com.android.calendar/calendars");
            File.WriteAllText(Path.Combine(dataDir, "calendar.txt"), calendarOutput);
            step++; progress.Update(step);

            // 备份内部存储目录结构
            PrintInfo("备份内部存储目录结构...");
            string sdcardList = GetAdbOutput("shell ls -la /sdcard/");
            File.WriteAllText(Path.Combine(dataDir, "sdcard_list.txt"), sdcardList);
            step++; progress.Update(step);

            // 创建备份信息文件
            var backupInfo = new Dictionary<string, object>
            {
                { "backup_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "device_model", isBetaMode ? virtualDevice["model"] : GetAdbOutput("shell getprop ro.product.model") },
                { "android_version", isBetaMode ? virtualDevice["android_version"] : GetAdbOutput("shell getprop ro.build.version.release") },
                { "app_count", packageNames.Count },
                { "backup_path", backupPath }
            };
            string infoJson = JsonSerializer.Serialize(backupInfo, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(backupPath, "backup_info.json"), infoJson);

            // 创建恢复脚本
            CreateRestoreScript(packageNames, apkDir);

            PrintSuccess("数据备份完成！");
            PrintInfo($"备份了 {packageNames.Count} 个应用 APK");
            PrintInfo($"备份文件保存在: {backupPath}");
            PrintInfo($"备份索引已创建，共 {backupIndex.Count} 个应用");

            Console.Write("是否打开备份文件夹？(y/n): ");
            if (Console.ReadLine()?.Trim().ToLower() == "y")
            {
                Process.Start("explorer.exe", backupPath);
            }

            hasBackup = true;
        }

        static void CreateRestoreScript(List<string> packageNames, string apkDir)
        {
            string scriptPath = Path.Combine(backupPath, "restore_apps.bat");
            using (StreamWriter sw = new StreamWriter(scriptPath))
            {
                sw.WriteLine("@echo off");
                sw.WriteLine("chcp 65001 >nul");
                sw.WriteLine("echo ========================================");
                sw.WriteLine("echo      正在恢复应用，请勿断开连接");
                sw.WriteLine("echo ========================================");
                sw.WriteLine("");

                int successCount = 0;
                int failCount = 0;

                foreach (string pkg in packageNames)
                {
                    string apkFile = Path.Combine(apkDir, $"{pkg}.apk");
                    sw.WriteLine($"echo 正在安装: {pkg}");
                    sw.WriteLine($"adb install -r \"{apkFile}\"");
                    sw.WriteLine("if %errorlevel% equ 0 (");
                    sw.WriteLine($"    echo [成功] {pkg}");
                    sw.WriteLine($"    set /a successCount+=1");
                    sw.WriteLine(") else (");
                    sw.WriteLine($"    echo [失败] {pkg}");
                    sw.WriteLine($"    set /a failCount+=1");
                    sw.WriteLine(")");
                    sw.WriteLine("echo.");
                }

                sw.WriteLine("echo ========================================");
                sw.WriteLine("echo      应用恢复完成！");
                sw.WriteLine($"echo       成功: %successCount% 个");
                sw.WriteLine($"echo       失败: %failCount% 个");
                sw.WriteLine("echo ========================================");
                sw.WriteLine("pause");
            }
            PrintInfo($"恢复脚本已创建: {scriptPath}");
        }

        // ========== 恢复数据功能 ==========
        static void RestoreData()
        {
            PrintHeader("恢复手机数据");

            if (!hasBackup || string.IsNullOrEmpty(backupPath) || !Directory.Exists(backupPath))
            {
                PrintError("未找到备份数据，请先使用选项14进行备份。");
                return;
            }

            PrintInfo($"正在从备份恢复数据...\n备份路径: {backupPath}");

            string infoFile = Path.Combine(backupPath, "backup_info.json");
            if (File.Exists(infoFile))
            {
                try
                {
                    string json = File.ReadAllText(infoFile);
                    var backupInfo = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    if (backupInfo != null)
                    {
                        PrintInfo($"备份时间: {backupInfo["backup_time"]}");
                        PrintInfo($"备份设备: {backupInfo["device_model"]}");
                        PrintInfo($"应用数量: {backupInfo["app_count"]}");
                    }
                }
                catch { }
            }

            if (!isBetaMode)
            {
                DeviceMode mode = GetDeviceMode();
                if (mode != DeviceMode.System)
                {
                    PrintError("恢复功能需要设备处于系统模式");
                    PrintInfo("请确保设备已开机并开启 USB 调试");
                    return;
                }
            }
            else
            {
                PrintInfo("[内测模式] 模拟恢复数据...");
            }

            string dataDir = Path.Combine(backupPath, "data");
            string apkDir = Path.Combine(backupPath, "apps");

            PrintInfo("\n请选择恢复方式：");
            Console.WriteLine("  1. 自动安装所有应用（使用 ADB install）");
            Console.WriteLine("  2. 手动恢复（打开备份文件夹自行操作）");
            Console.Write("请选择 (1/2): ");
            string choice = Console.ReadLine()?.Trim();

            if (choice == "1")
            {
                string[] apkFiles = Directory.GetFiles(apkDir, "*.apk");
                if (apkFiles.Length == 0)
                {
                    PrintError("未找到 APK 备份文件。");
                    return;
                }

                PrintInfo($"找到 {apkFiles.Length} 个应用备份，开始恢复...\n");

                var progress = new ProgressBar(apkFiles.Length, "应用恢复进度");
                int successCount = 0;
                int failCount = 0;

                for (int i = 0; i < apkFiles.Length; i++)
                {
                    string apkFile = apkFiles[i];
                    string fileName = Path.GetFileName(apkFile);
                    string pkgName = fileName.Replace(".apk", "");

                    PrintInfo($"安装: {pkgName}");
                    try
                    {
                        if (!isBetaMode)
                        {
                            RunCommand(adbPath, $"install -r \"{apkFile}\"");
                            PrintSuccess($"安装成功: {pkgName}");
                            successCount++;
                        }
                        else
                        {
                            PrintSuccess($"[模拟] 安装成功: {pkgName}");
                            successCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        PrintWarning($"安装失败 {pkgName}: {ex.Message}");
                        failCount++;
                    }
                    progress.Update(i + 1);
                }

                PrintSuccess($"应用恢复完成！成功: {successCount}，失败: {failCount}");
            }
            else if (choice == "2")
            {
                PrintInfo("打开备份文件夹，请手动操作...");
                Process.Start("explorer.exe", backupPath);
            }
            else
            {
                PrintError("无效选择。");
                return;
            }

            PrintInfo("\n恢复联系人、短信等数据...");
            PrintWarning("联系人、短信、通话记录等数据需要手动导入：");
            Console.WriteLine($"  联系人备份: {Path.Combine(dataDir, "contacts.txt")}");
            Console.WriteLine($"  短信备份: {Path.Combine(dataDir, "sms.txt")}");
            Console.WriteLine($"  通话记录备份: {Path.Combine(dataDir, "call_log.txt")}");
            Console.WriteLine($"  日历备份: {Path.Combine(dataDir, "calendar.txt")}");
            PrintInfo("请使用手机系统自带的导入功能或第三方工具恢复这些数据。");

            PrintSuccess("数据恢复流程完成！");
        }

        // ========== 刷机核心逻辑 ==========
        static void ProcessFlashFolder(bool forceMode)
        {
            PrintHeader($"{(forceMode ? "强力" : "正常")}刷机流程");
            Console.WriteLine("流程：自动进入 FastbootD → 检测 Bootloader 解锁 → 刷入系统 → 处理刷写结果\n");

            if (!isBetaMode)
            {
                if (!EnterFastbootD())
                    return;

                if (!EnsureBootloaderUnlocked())
                    return;
            }
            else
            {
                PrintInfo("[内测模式] 使用虚拟设备测试...");
            }

            string folder = GetFolderPathInput("请输入包含 .img 镜像文件的文件夹路径: ");
            if (string.IsNullOrEmpty(folder) || (!isBetaMode && !Directory.Exists(folder)))
            {
                PrintError("文件夹不存在或路径无效，操作取消。");
                return;
            }

            string[] imgFiles = isBetaMode ? new string[] { "test.img" } : Directory.GetFiles(folder, "*.img");
            if (imgFiles.Length == 0)
            {
                PrintError("所选文件夹中没有找到任何 .img 文件。");
                return;
            }

            List<string> systemFiles = new List<string>();
            List<string> basebandFiles = new List<string>();
            List<string> unknownFiles = new List<string>();
            Dictionary<string, string> fileToPartition = new Dictionary<string, string>();

            foreach (string imgPath in imgFiles)
            {
                string partition = GetPartitionFromFileName(imgPath);
                if (!string.IsNullOrEmpty(partition))
                {
                    fileToPartition[imgPath] = partition;
                    if (IsBasebandPartition(partition))
                        basebandFiles.Add(imgPath);
                    else
                        systemFiles.Add(imgPath);
                }
                else
                {
                    unknownFiles.Add(imgPath);
                    fileToPartition[imgPath] = "unknown";
                }
            }

            if (systemFiles.Count == 0 && basebandFiles.Count == 0 && unknownFiles.Count == 0)
            {
                PrintError("无法识别任何分区，请检查镜像文件。");
                return;
            }

            Console.WriteLine($"\n扫描到 {systemFiles.Count} 个系统镜像文件");
            Console.WriteLine($"扫描到 {basebandFiles.Count} 个基带镜像文件");
            if (unknownFiles.Count > 0) Console.WriteLine($"扫描到 {unknownFiles.Count} 个未识别镜像文件");

            DisplayImagesByCategory(systemFiles.Count, basebandFiles.Count, unknownFiles.Count);

            int mode = GetFlashMode();

            if (mode == 3)
            {
                PrintInfo("用户选择不刷入，跳过。");
                return;
            }

            List<string> allFilesToFlash = new List<string>();
            List<string> allPartitions = new List<string>();

            if (mode == 2)
            {
                foreach (var file in systemFiles) { allFilesToFlash.Add(file); allPartitions.Add(fileToPartition[file]); }
                foreach (var file in basebandFiles) { allFilesToFlash.Add(file); allPartitions.Add(fileToPartition[file]); }

                foreach (var file in unknownFiles)
                {
                    Console.WriteLine($"\n未识别镜像: {Path.GetFileName(file)}");
                    Console.Write("请输入该镜像对应的分区名称: ");
                    EnableConsolePaste();
                    string userPartition = Console.ReadLine()?.Trim().ToLower();
                    DisableConsolePaste(true);
                    userPartition = TrimPathQuotes(userPartition);
                    if (!string.IsNullOrEmpty(userPartition))
                    {
                        allFilesToFlash.Add(file);
                        allPartitions.Add(userPartition);
                        PrintSuccess($"已设置 {Path.GetFileName(file)} -> [{userPartition}]");
                    }
                    else
                    {
                        PrintWarning($"跳过 {Path.GetFileName(file)}");
                    }
                }
                PrintSuccess($"将刷写全部 {systemFiles.Count} 个系统镜像文件 + {basebandFiles.Count} 个基带镜像文件 + {allFilesToFlash.Count - systemFiles.Count - basebandFiles.Count} 个自定义镜像。");
            }
            else if (mode == 1)
            {
                if (systemFiles.Count > 0)
                {
                    Console.WriteLine($"\n【系统分区】（共 {systemFiles.Count} 个）");
                    for (int i = 0; i < systemFiles.Count; i++)
                        Console.WriteLine($"  {i + 1}. {Path.GetFileName(systemFiles[i])} -> [{fileToPartition[systemFiles[i]]}]");
                    Console.Write($"请选择要刷写的系统镜像（输入序号如 1,3,5 或输入 all 刷写全部，回车跳过）: ");
                    EnableConsolePaste();
                    string input = Console.ReadLine()?.Trim().ToLower();
                    DisableConsolePaste(true);
                    if (!string.IsNullOrEmpty(input))
                    {
                        if (input == "all")
                        {
                            foreach (var file in systemFiles) { allFilesToFlash.Add(file); allPartitions.Add(fileToPartition[file]); }
                        }
                        else
                        {
                            foreach (string part in input.Split(','))
                                if (int.TryParse(part.Trim(), out int idx) && idx >= 1 && idx <= systemFiles.Count)
                                {
                                    allFilesToFlash.Add(systemFiles[idx - 1]);
                                    allPartitions.Add(fileToPartition[systemFiles[idx - 1]]);
                                }
                        }
                    }
                }

                if (basebandFiles.Count > 0)
                {
                    Console.WriteLine($"\n【基带/固件分区】（共 {basebandFiles.Count} 个）");
                    for (int i = 0; i < basebandFiles.Count; i++)
                        Console.WriteLine($"  B{i + 1}. {Path.GetFileName(basebandFiles[i])} -> [{fileToPartition[basebandFiles[i]]}]");
                    Console.Write($"请选择要刷写的基带镜像（输入序号如 B1,B3,B5 或输入 all 刷写全部，回车跳过）: ");
                    EnableConsolePaste();
                    string input = Console.ReadLine()?.Trim().ToLower();
                    DisableConsolePaste(true);
                    if (!string.IsNullOrEmpty(input))
                    {
                        if (input == "all")
                        {
                            foreach (var file in basebandFiles) { allFilesToFlash.Add(file); allPartitions.Add(fileToPartition[file]); }
                        }
                        else
                        {
                            foreach (string part in input.Split(','))
                            {
                                string trimmed = part.Trim().ToLower();
                                if (trimmed.StartsWith("b")) trimmed = trimmed.Substring(1);
                                if (int.TryParse(trimmed, out int idx) && idx >= 1 && idx <= basebandFiles.Count)
                                {
                                    allFilesToFlash.Add(basebandFiles[idx - 1]);
                                    allPartitions.Add(fileToPartition[basebandFiles[idx - 1]]);
                                }
                            }
                        }
                    }
                }

                if (unknownFiles.Count > 0)
                {
                    Console.WriteLine($"\n⚠️ 【未识别分区】（共 {unknownFiles.Count} 个）");
                    for (int i = 0; i < unknownFiles.Count; i++)
                        Console.WriteLine($"  {i + 1}. {Path.GetFileName(unknownFiles[i])} - 无法自动识别");
                    Console.Write($"请选择要刷写的未识别镜像（输入序号如 1,3,5 或输入 all 刷写全部，回车跳过）: ");
                    EnableConsolePaste();
                    string input = Console.ReadLine()?.Trim().ToLower();
                    DisableConsolePaste(true);
                    if (!string.IsNullOrEmpty(input))
                    {
                        List<int> unknownIndices = new List<int>();
                        if (input == "all")
                        {
                            for (int i = 0; i < unknownFiles.Count; i++) unknownIndices.Add(i);
                        }
                        else
                        {
                            foreach (string part in input.Split(','))
                                if (int.TryParse(part.Trim(), out int idx) && idx >= 1 && idx <= unknownFiles.Count) unknownIndices.Add(idx - 1);
                        }

                        foreach (int idx in unknownIndices)
                        {
                            string file = unknownFiles[idx];
                            Console.Write($"\n请输入 {Path.GetFileName(file)} 对应的分区名称: ");
                            EnableConsolePaste();
                            string userPartition = Console.ReadLine()?.Trim().ToLower();
                            DisableConsolePaste(true);
                            userPartition = TrimPathQuotes(userPartition);
                            if (!string.IsNullOrEmpty(userPartition))
                            {
                                allFilesToFlash.Add(file);
                                allPartitions.Add(userPartition);
                                PrintSuccess($"已设置 {Path.GetFileName(file)} -> [{userPartition}]");
                            }
                            else
                            {
                                PrintWarning($"跳过 {Path.GetFileName(file)}");
                            }
                        }
                    }
                }
            }

            if (allFilesToFlash.Count == 0)
            {
                PrintWarning("未选择任何镜像，跳过刷写。");
                return;
            }

            PrintInfo($"共选择 {allFilesToFlash.Count} 个镜像文件进行刷写");

            bool hasBasebandFlag = false;
            foreach (var file in allFilesToFlash)
            {
                string partition = fileToPartition.ContainsKey(file) ? fileToPartition[file] : "";
                if (IsBasebandPartition(partition)) { hasBasebandFlag = true; break; }
            }

            bool hasSystemFlag = allFilesToFlash.Count > (hasBasebandFlag ? 1 : 0);

            if (hasSystemFlag)
            {
                PrintInfo("开始刷写系统分区...");

                var progress = new ProgressBar(allFilesToFlash.Count, "系统分区刷写进度");
                for (int step = 0; step < allFilesToFlash.Count; step++)
                {
                    string imgPath = allFilesToFlash[step];
                    string partition = allPartitions[step];
                    Console.WriteLine($"\n刷写 {Path.GetFileName(imgPath)} 到 [{partition}] 分区...");
                    try
                    {
                        if (!isBetaMode)
                            RunCommand(fastbootPath, $"flash {partition} \"{imgPath}\"");
                        else
                            PrintSuccess($"[模拟] 刷写 {partition} 完成");
                        PrintSuccess($"刷写 {partition} 完成");
                    }
                    catch { PrintError($"刷写 {partition} 失败"); if (!forceMode) break; }
                    progress.Update(step + 1);
                }
                PrintSuccess("系统分区刷写完成！");
            }

            // 基带分区需要在 Fastboot 模式刷写
            if (hasBasebandFlag)
            {
                PrintInfo("\n准备刷写基带分区，需要切换到 Fastboot 模式...");
                if (!isBetaMode)
                {
                    if (!EnterFastboot())
                    {
                        PrintError("无法进入 Fastboot 模式，跳过基带刷写");
                    }
                    else
                    {
                        PrintSuccess("开始刷写基带分区...");
                        var progress = new ProgressBar(basebandFiles.Count, "基带刷写进度");
                        for (int step = 0; step < basebandFiles.Count; step++)
                        {
                            string imgPath = basebandFiles[step];
                            string partition = fileToPartition[imgPath];
                            Console.WriteLine($"\n刷写 {Path.GetFileName(imgPath)} 到 [{partition}] 分区...");
                            RunCommand(fastbootPath, $"flash {partition} \"{imgPath}\"");
                            progress.Update(step + 1);
                        }
                        PrintSuccess("基带分区刷写完成！");
                    }
                }
                else
                {
                    PrintInfo("[内测模式] 模拟基带刷写...");
                    PrintSuccess("模拟基带刷写完成！");
                }
            }

            // 清除数据和重启
            if (hasSystemFlag && !forceMode)
            {
                Console.Write("\n是否清除用户数据？(y/n): ");
                if (Console.ReadLine()?.Trim().ToLower() == "y")
                {
                    PrintInfo("正在清除数据...");
                    if (!isBetaMode)
                        RunCommand(fastbootPath, "-w");
                    else
                        PrintInfo("[内测模式] 模拟清除用户数据");
                }
                else
                {
                    if (hasBackup && !string.IsNullOrEmpty(backupPath) && Directory.Exists(backupPath))
                    {
                        Console.Write("\n检测到有备份数据，是否恢复数据？(y/n): ");
                        if (Console.ReadLine()?.Trim().ToLower() == "y")
                        {
                            RestoreData();
                        }
                    }
                }
            }

            Console.Write("\n是否重启设备？(y/n): ");
            if (Console.ReadLine()?.Trim().ToLower() == "y")
            {
                if (!isBetaMode)
                    SmartReboot();
                else
                    PrintInfo("[内测模式] 模拟重启设备");
            }
            PrintSuccess("刷机流程完成！");
        }

        static void NormalFlash() => ProcessFlashFolder(false);
        static void ForceFlashRepairSuper() => ProcessFlashFolder(true);

        static void FixFastbootState()
        {
            PrintHeader("修复 Fastboot 状态异常");
            PrintInfo("尝试修复 Fastboot 状态异常...");

            if (!isBetaMode)
            {
                if (!EnterFastboot())
                    return;

                RunCommand(fastbootPath, "oem reboot-recovery");
                Thread.Sleep(3000);
            }
            else
            {
                PrintInfo("[内测模式] 模拟修复 Fastboot 状态");
            }

            PrintInfo("若设备仍异常，可尝试手动操作。");
        }

        static void ExtractAndFlashPayload()
        {
            PrintHeader("解包 OTA 包并刷写");
            PrintInfo("此功能需要 payload-dumper-go 工具支持。\n");

            if (!isBetaMode && !File.Exists(payloadDumperPath))
            {
                PrintError($"未找到 payload-dumper-go.exe，当前路径：{payloadDumperPath}");
                Console.WriteLine("请从 https://github.com/ssut/payload-dumper-go/releases 下载并放置于程序目录或'拓展'文件夹。");
                Console.Write("是否手动指定路径？(y/n): ");
                if (Console.ReadLine()?.Trim().ToLower() == "y")
                {
                    Console.Write("请输入 payload-dumper-go 完整路径: ");
                    EnableConsolePaste();
                    string inputPath = Console.ReadLine()?.Trim();
                    DisableConsolePaste(true);
                    inputPath = TrimPathQuotes(inputPath);
                    if (!string.IsNullOrEmpty(inputPath) && File.Exists(inputPath)) { payloadDumperPath = inputPath; SaveConfig(); PrintSuccess("路径已保存。"); }
                    else { PrintError("文件不存在，操作取消。"); return; }
                }
                else { PrintInfo("按任意键返回主菜单..."); Console.ReadKey(); return; }
            }

            if (isBetaMode)
            {
                PrintInfo("[内测模式] 模拟解包 OTA 包...");
                string testOutputDir = Path.Combine(logFolder, "test_payload_output");
                Directory.CreateDirectory(testOutputDir);
                PrintSuccess("模拟解包完成！");
                string[] testFiles = new string[] { Path.Combine(testOutputDir, "boot.img"), Path.Combine(testOutputDir, "system.img") };
                ProcessFlashFolderFromFiles(testFiles, testOutputDir, false);
                return;
            }

            Console.Write("请输入 OTA 包完整路径（.zip 或 .bin 文件）: ");
            EnableConsolePaste();
            string otaPath = Console.ReadLine()?.Trim();
            DisableConsolePaste(true);
            otaPath = TrimPathQuotes(otaPath);
            if (!File.Exists(otaPath)) { PrintError("文件不存在，操作取消。"); return; }

            string payloadPath = otaPath;
            if (Path.GetExtension(otaPath).ToLower() == ".zip")
            {
                PrintInfo("检测到 ZIP 包，尝试查找 payload.bin...");
                string tempDir = Path.Combine(Path.GetTempPath(), "ColorOS_Extract");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);
                try
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(otaPath, tempDir);
                    payloadPath = Path.Combine(tempDir, "payload.bin");
                    if (!File.Exists(payloadPath)) { PrintError("ZIP 包中未找到 payload.bin。"); return; }
                }
                catch (Exception ex) { PrintError($"解压失败: {ex.Message}"); return; }
            }
            else if (Path.GetExtension(otaPath).ToLower() != ".bin") { PrintError("不支持的文件格式，请选择 .zip 或 .bin 文件。"); return; }

            string outputDir = Path.Combine(logFolder, $"payload_{DateTime.Now:yyyyMMddHHmmss}");
            Directory.CreateDirectory(outputDir);
            PrintInfo($"正在解包 payload.bin 到 {outputDir}...");

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = payloadDumperPath,
                Arguments = $"-o \"{outputDir}\" \"{payloadPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                string error = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (!string.IsNullOrEmpty(output)) Console.WriteLine(output);
                if (!string.IsNullOrEmpty(error)) PrintError(error);
            }

            string[] imgFiles = Directory.GetFiles(outputDir, "*.img");
            if (imgFiles.Length == 0) { PrintError("解包未生成任何镜像文件，操作取消。"); return; }

            ProcessFlashFolderFromFiles(imgFiles, outputDir, false);
        }

        static void ProcessFlashFolderFromFiles(string[] imgFiles, string sourceFolder, bool forceMode)
        {
            List<string> systemFiles = new List<string>();
            List<string> basebandFiles = new List<string>();
            List<string> unknownFiles = new List<string>();
            Dictionary<string, string> fileToPartition = new Dictionary<string, string>();

            foreach (string imgPath in imgFiles)
            {
                string partition = GetPartitionFromFileName(imgPath);
                if (!string.IsNullOrEmpty(partition))
                {
                    fileToPartition[imgPath] = partition;
                    if (IsBasebandPartition(partition))
                        basebandFiles.Add(imgPath);
                    else
                        systemFiles.Add(imgPath);
                }
                else
                {
                    unknownFiles.Add(imgPath);
                    fileToPartition[imgPath] = "unknown";
                }
            }

            Console.WriteLine($"\n扫描到 {systemFiles.Count} 个系统镜像文件，{basebandFiles.Count} 个基带镜像文件，{unknownFiles.Count} 个未识别镜像文件");

            DisplayImagesByCategory(systemFiles.Count, basebandFiles.Count, unknownFiles.Count);

            int mode = GetFlashMode();

            if (mode == 3)
            {
                PrintInfo("用户选择不刷入，跳过。");
                return;
            }

            List<string> allFilesToFlash = new List<string>();
            List<string> allPartitions = new List<string>();

            if (mode == 2)
            {
                foreach (var file in systemFiles) { allFilesToFlash.Add(file); allPartitions.Add(fileToPartition[file]); }
                foreach (var file in basebandFiles) { allFilesToFlash.Add(file); allPartitions.Add(fileToPartition[file]); }

                foreach (var file in unknownFiles)
                {
                    Console.WriteLine($"\n未识别镜像: {Path.GetFileName(file)}");
                    Console.Write("请输入该镜像对应的分区名称: ");
                    EnableConsolePaste();
                    string userPartition = Console.ReadLine()?.Trim().ToLower();
                    DisableConsolePaste(true);
                    userPartition = TrimPathQuotes(userPartition);
                    if (!string.IsNullOrEmpty(userPartition))
                    {
                        allFilesToFlash.Add(file);
                        allPartitions.Add(userPartition);
                        PrintSuccess($"已设置 {Path.GetFileName(file)} -> [{userPartition}]");
                    }
                    else
                    {
                        PrintWarning($"跳过 {Path.GetFileName(file)}");
                    }
                }
                PrintSuccess($"将刷写全部 {systemFiles.Count} 个系统镜像文件 + {basebandFiles.Count} 个基带镜像文件 + {allFilesToFlash.Count - systemFiles.Count - basebandFiles.Count} 个自定义镜像。");
            }
            else if (mode == 1)
            {
                if (systemFiles.Count > 0)
                {
                    Console.WriteLine($"\n【系统分区】（共 {systemFiles.Count} 个）");
                    for (int i = 0; i < systemFiles.Count; i++)
                        Console.WriteLine($"  {i + 1}. {Path.GetFileName(systemFiles[i])} -> [{fileToPartition[systemFiles[i]]}]");
                    Console.Write($"请选择要刷写的系统镜像（输入序号如 1,3,5 或输入 all 刷写全部，回车跳过）: ");
                    EnableConsolePaste();
                    string input = Console.ReadLine()?.Trim().ToLower();
                    DisableConsolePaste(true);
                    if (!string.IsNullOrEmpty(input))
                    {
                        if (input == "all")
                        {
                            foreach (var file in systemFiles) { allFilesToFlash.Add(file); allPartitions.Add(fileToPartition[file]); }
                        }
                        else
                        {
                            foreach (string part in input.Split(','))
                                if (int.TryParse(part.Trim(), out int idx) && idx >= 1 && idx <= systemFiles.Count)
                                {
                                    allFilesToFlash.Add(systemFiles[idx - 1]);
                                    allPartitions.Add(fileToPartition[systemFiles[idx - 1]]);
                                }
                        }
                    }
                }

                if (basebandFiles.Count > 0)
                {
                    Console.WriteLine($"\n【基带/固件分区】（共 {basebandFiles.Count} 个）");
                    for (int i = 0; i < basebandFiles.Count; i++)
                        Console.WriteLine($"  B{i + 1}. {Path.GetFileName(basebandFiles[i])} -> [{fileToPartition[basebandFiles[i]]}]");
                    Console.Write($"请选择要刷写的基带镜像（输入序号如 B1,B3,B5 或输入 all 刷写全部，回车跳过）: ");
                    EnableConsolePaste();
                    string input = Console.ReadLine()?.Trim().ToLower();
                    DisableConsolePaste(true);
                    if (!string.IsNullOrEmpty(input))
                    {
                        if (input == "all")
                        {
                            foreach (var file in basebandFiles) { allFilesToFlash.Add(file); allPartitions.Add(fileToPartition[file]); }
                        }
                        else
                        {
                            foreach (string part in input.Split(','))
                            {
                                string trimmed = part.Trim().ToLower();
                                if (trimmed.StartsWith("b")) trimmed = trimmed.Substring(1);
                                if (int.TryParse(trimmed, out int idx) && idx >= 1 && idx <= basebandFiles.Count)
                                {
                                    allFilesToFlash.Add(basebandFiles[idx - 1]);
                                    allPartitions.Add(fileToPartition[basebandFiles[idx - 1]]);
                                }
                            }
                        }
                    }
                }

                if (unknownFiles.Count > 0)
                {
                    Console.WriteLine($"\n⚠️ 【未识别分区】（共 {unknownFiles.Count} 个）");
                    for (int i = 0; i < unknownFiles.Count; i++)
                        Console.WriteLine($"  {i + 1}. {Path.GetFileName(unknownFiles[i])} - 无法自动识别");
                    Console.Write($"请选择要刷写的未识别镜像（输入序号如 1,3,5 或输入 all 刷写全部，回车跳过）: ");
                    EnableConsolePaste();
                    string input = Console.ReadLine()?.Trim().ToLower();
                    DisableConsolePaste(true);
                    if (!string.IsNullOrEmpty(input))
                    {
                        List<int> unknownIndices = new List<int>();
                        if (input == "all")
                        {
                            for (int i = 0; i < unknownFiles.Count; i++) unknownIndices.Add(i);
                        }
                        else
                        {
                            foreach (string part in input.Split(','))
                                if (int.TryParse(part.Trim(), out int idx) && idx >= 1 && idx <= unknownFiles.Count) unknownIndices.Add(idx - 1);
                        }

                        foreach (int idx in unknownIndices)
                        {
                            string file = unknownFiles[idx];
                            Console.Write($"\n请输入 {Path.GetFileName(file)} 对应的分区名称: ");
                            EnableConsolePaste();
                            string userPartition = Console.ReadLine()?.Trim().ToLower();
                            DisableConsolePaste(true);
                            userPartition = TrimPathQuotes(userPartition);
                            if (!string.IsNullOrEmpty(userPartition))
                            {
                                allFilesToFlash.Add(file);
                                allPartitions.Add(userPartition);
                                PrintSuccess($"已设置 {Path.GetFileName(file)} -> [{userPartition}]");
                            }
                            else
                            {
                                PrintWarning($"跳过 {Path.GetFileName(file)}");
                            }
                        }
                    }
                }
            }

            if (allFilesToFlash.Count == 0)
            {
                PrintWarning("未选择任何镜像，跳过刷写。");
                return;
            }

            PrintInfo($"共选择 {allFilesToFlash.Count} 个镜像文件进行刷写");

            bool hasBasebandFlag = false;
            foreach (var file in allFilesToFlash)
            {
                string partition = fileToPartition.ContainsKey(file) ? fileToPartition[file] : "";
                if (IsBasebandPartition(partition)) { hasBasebandFlag = true; break; }
            }

            bool hasSystemFlag = allFilesToFlash.Count > (hasBasebandFlag ? 1 : 0);

            if (hasSystemFlag)
            {
                if (!isBetaMode)
                {
                    if (!EnterFastbootD())
                        return;
                }

                PrintInfo("开始刷写系统分区...");

                var progress = new ProgressBar(allFilesToFlash.Count, "系统分区刷写进度");
                for (int step = 0; step < allFilesToFlash.Count; step++)
                {
                    string imgPath = allFilesToFlash[step];
                    string partition = allPartitions[step];
                    Console.WriteLine($"\n刷写 {Path.GetFileName(imgPath)} 到 [{partition}] 分区...");
                    try
                    {
                        if (!isBetaMode)
                            RunCommand(fastbootPath, $"flash {partition} \"{imgPath}\"");
                        else
                            PrintSuccess($"[模拟] 刷写 {partition} 完成");
                        PrintSuccess($"刷写 {partition} 完成");
                    }
                    catch { PrintError($"刷写 {partition} 失败"); if (!forceMode) break; }
                    progress.Update(step + 1);
                }
                PrintSuccess("系统分区刷写完成！");
            }

            if (hasBasebandFlag)
            {
                PrintInfo("\n准备刷写基带分区，需要切换到 Fastboot 模式...");
                if (!isBetaMode)
                {
                    if (!EnterFastboot())
                    {
                        PrintError("无法进入 Fastboot 模式，跳过基带刷写");
                    }
                    else
                    {
                        PrintSuccess("开始刷写基带分区...");
                        var progress = new ProgressBar(basebandFiles.Count, "基带刷写进度");
                        for (int step = 0; step < basebandFiles.Count; step++)
                        {
                            string imgPath = basebandFiles[step];
                            string partition = fileToPartition[imgPath];
                            Console.WriteLine($"\n刷写 {Path.GetFileName(imgPath)} 到 [{partition}] 分区...");
                            RunCommand(fastbootPath, $"flash {partition} \"{imgPath}\"");
                            progress.Update(step + 1);
                        }
                        PrintSuccess("基带分区刷写完成！");
                    }
                }
                else
                {
                    PrintInfo("[内测模式] 模拟基带刷写...");
                    PrintSuccess("模拟基带刷写完成！");
                }
            }

            if (hasSystemFlag && !forceMode)
            {
                Console.Write("\n是否清除用户数据？(y/n): ");
                if (Console.ReadLine()?.Trim().ToLower() == "y")
                {
                    PrintInfo("正在清除数据...");
                    if (!isBetaMode)
                        RunCommand(fastbootPath, "-w");
                    else
                        PrintInfo("[内测模式] 模拟清除用户数据");
                }
                else
                {
                    if (hasBackup && !string.IsNullOrEmpty(backupPath) && Directory.Exists(backupPath))
                    {
                        Console.Write("\n检测到有备份数据，是否恢复数据？(y/n): ");
                        if (Console.ReadLine()?.Trim().ToLower() == "y")
                        {
                            RestoreData();
                        }
                    }
                }
            }

            Console.Write("\n是否重启设备？(y/n): ");
            if (Console.ReadLine()?.Trim().ToLower() == "y")
            {
                if (!isBetaMode)
                    SmartReboot();
                else
                    PrintInfo("[内测模式] 模拟重启设备");
            }
            PrintSuccess("刷机流程完成！");
        }

        static void FastbootDAutoFlash()
        {
            PrintHeader("纯 FastbootD 自动刷机");

            if (!isBetaMode)
            {
                if (!EnterFastbootD())
                    return;
            }
            else
            {
                PrintInfo("[内测模式] 使用虚拟设备测试...");
            }

            string folder = GetFolderPathInput("请输入包含 .img 镜像文件的文件夹路径: ");
            if (string.IsNullOrEmpty(folder) || (!isBetaMode && !Directory.Exists(folder))) { PrintError("文件夹不存在或路径无效。"); return; }

            string[] imgFiles = isBetaMode ? new string[] { "test.img" } : Directory.GetFiles(folder, "*.img");
            if (imgFiles.Length == 0) { PrintError("没有找到任何 .img 文件。"); return; }

            ProcessFlashFolderFromFiles(imgFiles, folder, false);
        }

        static void AdbScreenMirror()
        {
            PrintHeader("ADB 投屏");

            if (!isBetaMode)
            {
                DeviceMode mode = GetDeviceMode();
                if (mode != DeviceMode.System)
                {
                    PrintError("投屏功能需要设备处于系统模式");
                    PrintInfo("请确保设备已开机并开启 USB 调试");
                    return;
                }
            }
            else
            {
                PrintInfo("[内测模式] 模拟 ADB 投屏...");
                PrintSuccess("模拟投屏已启动！");
                PrintInfo("按任意键返回主菜单...");
                Console.ReadKey();
                return;
            }

            if (!File.Exists(scrcpyPath))
            {
                PrintError($"未找到 scrcpy.exe，当前路径：{scrcpyPath}");
                Console.WriteLine("请从 https://github.com/Genymobile/scrcpy/releases 下载并放置于程序目录或'拓展'文件夹。");
                Console.Write("是否手动指定路径？(y/n): ");
                if (Console.ReadLine()?.Trim().ToLower() == "y")
                {
                    Console.Write("请输入 scrcpy.exe 完整路径: ");
                    EnableConsolePaste();
                    string inputPath = Console.ReadLine()?.Trim();
                    DisableConsolePaste(true);
                    inputPath = TrimPathQuotes(inputPath);
                    if (!string.IsNullOrEmpty(inputPath) && File.Exists(inputPath)) { scrcpyPath = inputPath; SaveConfig(); PrintSuccess("路径已保存。"); }
                    else { PrintError("文件不存在，操作取消。"); return; }
                }
                else { PrintInfo("按任意键返回主菜单..."); Console.ReadKey(); return; }
            }

            PrintInfo("正在启动投屏...");
            Process.Start(new ProcessStartInfo { FileName = scrcpyPath, Arguments = "--stay-awake --turn-screen-off", UseShellExecute = true, CreateNoWindow = false });
            PrintInfo("投屏已启动，按任意键返回主菜单...");
            Console.ReadKey();
        }

        static void AdbSideload()
        {
            PrintHeader("ADB 线刷");

            if (!isBetaMode)
            {
                DeviceMode mode = GetDeviceMode();
                if (mode != DeviceMode.System)
                {
                    PrintError("线刷功能需要设备处于系统模式");
                    PrintInfo("请确保设备已开机并开启 USB 调试");
                    return;
                }
            }
            else
            {
                PrintInfo("[内测模式] 模拟 ADB 线刷...");
                PrintSuccess("模拟线刷完成！");
                return;
            }

            string zipPath = GetFilePathInput("请输入要刷入的 ZIP 包完整路径: ");
            if (!File.Exists(zipPath)) { PrintError("文件不存在，操作取消。"); return; }

            PrintInfo("正在重启设备到 Recovery 模式...");
            RunCommand(adbPath, "reboot recovery");
            Thread.Sleep(5000);

            PrintInfo("等待设备进入 Recovery 模式...");
            if (!WaitForDeviceMode(DeviceMode.Recovery, 30))
            {
                PrintError("无法进入 Recovery 模式");
                return;
            }

            PrintWarning("请在设备上选择 'Apply update from ADB' 选项，然后按任意键继续...");
            Console.ReadKey();
            RunCommand(adbPath, $"sideload \"{zipPath}\"");
            PrintSuccess("刷写完成。");
            Console.Write("是否重启设备？(y/n): ");
            if (Console.ReadLine()?.Trim().ToLower() == "y") SmartReboot();
        }

        static void FlashBasebandOnly()
        {
            PrintHeader("基带刷写");

            if (!isBetaMode)
            {
                if (!EnterFastboot())
                    return;

                if (!EnsureBootloaderUnlocked())
                    return;
            }
            else
            {
                PrintInfo("[内测模式] 模拟基带刷写...");
                PrintSuccess("模拟基带刷写完成！");
                return;
            }

            string folder = GetFolderPathInput("请输入包含基带镜像文件的文件夹路径: ");
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { PrintError("文件夹不存在或路径无效。"); return; }

            string[] imgFiles = Directory.GetFiles(folder, "*.img");
            List<string> basebandFiles = new List<string>();
            Dictionary<string, string> fileToPartition = new Dictionary<string, string>();

            foreach (string imgPath in imgFiles)
            {
                string partition = GetPartitionFromFileName(imgPath);
                if (!string.IsNullOrEmpty(partition) && IsBasebandPartition(partition))
                {
                    basebandFiles.Add(imgPath);
                    fileToPartition[imgPath] = partition;
                }
            }

            if (basebandFiles.Count == 0) { PrintError("未找到基带镜像文件。"); return; }

            Console.WriteLine("\n基带镜像文件：");
            for (int i = 0; i < basebandFiles.Count; i++)
                Console.WriteLine($"  {i + 1}. {Path.GetFileName(basebandFiles[i])} -> [{fileToPartition[basebandFiles[i]]}]");

            Console.Write("\n请选择要刷写的镜像（输入序号如 1,3,5 或输入 all 刷写全部）: ");
            EnableConsolePaste();
            string input = Console.ReadLine()?.Trim().ToLower();
            DisableConsolePaste(true);
            if (string.IsNullOrEmpty(input)) { PrintError("操作取消。"); return; }

            List<int> indices = new List<int>();
            if (input == "all") { for (int i = 0; i < basebandFiles.Count; i++) indices.Add(i); PrintSuccess($"将刷写全部 {basebandFiles.Count} 个基带镜像文件。"); }
            else
            {
                foreach (string part in input.Split(','))
                    if (int.TryParse(part.Trim(), out int idx) && idx >= 1 && idx <= basebandFiles.Count) indices.Add(idx - 1);
                if (indices.Count == 0) { PrintError("无效选择，操作取消。"); return; }
            }

            PrintSuccess("开始刷写基带分区...");
            var progress = new ProgressBar(indices.Count, "基带刷写进度");
            for (int step = 0; step < indices.Count; step++)
            {
                int idx = indices[step];
                string imgPath = basebandFiles[idx];
                string partition = fileToPartition[imgPath];
                Console.WriteLine($"\n刷写 {Path.GetFileName(imgPath)} 到 [{partition}] 分区...");
                RunCommand(fastbootPath, $"flash {partition} \"{imgPath}\"");
                progress.Update(step + 1);
            }
            PrintSuccess("基带刷写完成！");
            Console.Write("\n是否重启设备？(y/n): ");
            if (Console.ReadLine()?.Trim().ToLower() == "y") SmartReboot();
        }

        static void OpenCMD() => Process.Start("cmd.exe");
        static void OpenLogFolder() { if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder); Process.Start("explorer.exe", Path.GetFullPath(logFolder)); PrintSuccess("已打开日志文件夹。"); }

        static void GlobalSettings()
        {
            PrintHeader("全局设置");
            SetColor(InfoColor);
            Console.WriteLine($"当前 ADB 路径: {adbPath}");
            Console.WriteLine($"当前 Fastboot 路径: {fastbootPath}");
            Console.WriteLine($"当前 payload-dumper-go 路径: {payloadDumperPath}");
            Console.WriteLine($"当前 scrcpy 路径: {scrcpyPath}");
            ResetColor();
            Console.WriteLine("\n1. 修改 ADB/Fastboot 路径");
            Console.WriteLine("2. 重置为默认（使用 PATH 环境变量）");
            Console.WriteLine("3. 修改 payload-dumper-go 路径");
            Console.WriteLine("4. 修改 scrcpy 路径");
            Console.Write("请选择: ");
            string opt = Console.ReadLine()?.Trim();
            if (opt == "1") ConfigureToolPaths();
            else if (opt == "2") { adbPath = "adb.exe"; fastbootPath = "fastboot.exe"; PrintSuccess("已重置为默认路径。"); }
            else if (opt == "3")
            {
                Console.Write("请输入 payload-dumper-go 完整路径: ");
                EnableConsolePaste();
                string input = Console.ReadLine()?.Trim();
                DisableConsolePaste(true);
                input = TrimPathQuotes(input);
                if (!string.IsNullOrEmpty(input) && File.Exists(input)) { payloadDumperPath = input; SaveConfig(); PrintSuccess("路径已保存。"); }
                else PrintError("文件不存在，路径未修改。");
            }
            else if (opt == "4")
            {
                Console.Write("请输入 scrcpy 完整路径: ");
                EnableConsolePaste();
                string input = Console.ReadLine()?.Trim();
                DisableConsolePaste(true);
                input = TrimPathQuotes(input);
                if (!string.IsNullOrEmpty(input) && File.Exists(input)) { scrcpyPath = input; SaveConfig(); PrintSuccess("路径已保存。"); }
                else PrintError("文件不存在，路径未修改。");
            }
            else PrintError("无效选择。");
        }

        static void ShowHelp()
        {
            PrintHeader("使用说明");
            Console.WriteLine("1. 确保设备已开启 USB 调试并连接电脑。");
            Console.WriteLine("2. 所有涉及刷写的操作请提前备份重要数据。");
            Console.WriteLine("3. 解锁 Bootloader 会清除所有数据，请谨慎操作。");
            Console.WriteLine("4. 选项1/3/4/5 会自动进入 FastbootD 模式并检测设备状态。");
            Console.WriteLine("5. 选项14 可备份手机数据（联系人、短信、应用等）");
            Console.WriteLine("6. 选项15 可恢复手机数据");
            Console.WriteLine("7. 选项B 可单独刷写基带分区。");
            Console.WriteLine("8. 选项T 可临时启动 Recovery（测试功能，适用于 A/B/VAB 设备）");
            Console.WriteLine("9. 选项4需要 payload-dumper-go 工具，从 GitHub 下载。");
            Console.WriteLine("10. 选项6需要 scrcpy 工具，从 GitHub 下载。");
            Console.WriteLine("11. 可将工具放入'拓展'文件夹，程序会自动识别（支持子目录）。");
            Console.WriteLine("12. 内测模式密码: neice123456，可模拟设备进行测试");
            Console.WriteLine("13. 本工具会自动检测设备模式，无需手动确认。");
            Console.WriteLine("14. 本工具仅为辅助，操作风险自负。");
        }

        static void UnlockBootloaderMenu()
        {
            PrintHeader("解锁 Bootloader");

            if (!isBetaMode)
            {
                if (!EnterFastboot())
                    return;

                if (CheckBootloaderUnlockedInFastboot())
                {
                    PrintWarning("设备已解锁 Bootloader。非官方系统不可回锁。");
                    return;
                }
            }
            else
            {
                PrintInfo("[内测模式] 模拟解锁 Bootloader...");
                PrintSuccess("模拟解锁成功！");
                return;
            }

            Console.Write("是否要解锁 Bootloader？这将清除所有数据。确认？(yes/no): ");
            if (Console.ReadLine()?.Trim().ToLower() != "yes") { PrintError("操作取消。"); return; }

            Console.WriteLine("请选择解锁命令：");
            Console.WriteLine("1. fastboot oem unlock");
            Console.WriteLine("2. fastboot flashing unlock");
            Console.Write("请输入数字: ");
            string method = Console.ReadLine()?.Trim();
            if (method == "1") RunCommand(fastbootPath, "oem unlock");
            else if (method == "2") RunCommand(fastbootPath, "flashing unlock");
            else PrintError("无效选择，操作取消。");
            PrintInfo("解锁命令已执行，请根据设备屏幕提示完成操作。");
        }

        static void GetDeviceInfo()
        {
            PrintHeader("设备信息");

            if (isBetaMode)
            {
                ShowVirtualDeviceInfo();
                return;
            }

            DeviceMode mode = GetDeviceMode();
            PrintInfo($"当前设备模式: {mode}");

            if (mode == DeviceMode.System)
            {
                PrintInfo("设备型号", GetAdbOutput("shell getprop ro.product.model"));
                PrintInfo("品牌", GetAdbOutput("shell getprop ro.product.brand"));
                PrintInfo("制造商", GetAdbOutput("shell getprop ro.product.manufacturer"));
                PrintInfo("设备代号", GetAdbOutput("shell getprop ro.product.device"));
                PrintInfo("Android 版本", GetAdbOutput("shell getprop ro.build.version.release"));
                PrintInfo("SDK 版本", GetAdbOutput("shell getprop ro.build.version.sdk"));
                PrintInfo("安全补丁", GetAdbOutput("shell getprop ro.build.version.security_patch"));
                PrintInfo("构建 ID", GetAdbOutput("shell getprop ro.build.id"));
                PrintInfo("CPU 架构", GetAdbOutput("shell getprop ro.product.cpu.abi"));
                PrintInfo("屏幕分辨率", GetAdbOutput("shell wm size"));
                PrintInfo("屏幕密度", GetAdbOutput("shell wm density"));
            }
            else if (mode == DeviceMode.Fastboot || mode == DeviceMode.FastbootD)
            {
                PrintInfo("设备处于 Fastboot/FastbootD 模式");
                string varOutput = RunFastbootCommand("getvar all");
                if (!string.IsNullOrEmpty(varOutput))
                {
                    // 提取关键信息
                    var lines = varOutput.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.Contains("product") || line.Contains("version") || line.Contains("secure"))
                            Console.WriteLine($"  {line.Trim()}");
                    }
                }
            }
            else
            {
                PrintError("未检测到设备连接");
            }
        }

        static void PrintInfo(string label, string value) { if (string.IsNullOrEmpty(value)) value = "未知"; Console.WriteLine($"{label.PadRight(20)}: {value}"); }

        static bool CheckTools() => CheckCommandExists(adbPath, "version") && CheckCommandExists(fastbootPath, "--version");

        static bool CheckCommandExists(string command, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo { FileName = command, Arguments = args, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process p = Process.Start(psi)) { p.WaitForExit(3000); return p.ExitCode == 0; }
            }
            catch { return false; }
        }

        static void ConfigureToolPaths()
        {
            Console.Write("请输入 ADB 完整路径（如 C:\\platform-tools\\adb.exe）: ");
            EnableConsolePaste();
            string input = Console.ReadLine()?.Trim();
            DisableConsolePaste(true);
            input = TrimPathQuotes(input);
            if (!string.IsNullOrEmpty(input) && File.Exists(input)) adbPath = input;
            else if (!string.IsNullOrEmpty(input)) Console.WriteLine("文件不存在，将使用默认配置。");

            Console.Write("请输入 Fastboot 完整路径（如 C:\\platform-tools\\fastboot.exe）: ");
            EnableConsolePaste();
            input = Console.ReadLine()?.Trim();
            DisableConsolePaste(true);
            input = TrimPathQuotes(input);
            if (!string.IsNullOrEmpty(input) && File.Exists(input)) fastbootPath = input;
            else if (!string.IsNullOrEmpty(input)) Console.WriteLine("文件不存在，将使用默认配置。");

            Console.Write("请输入 payload-dumper-go 完整路径: ");
            EnableConsolePaste();
            input = Console.ReadLine()?.Trim();
            DisableConsolePaste(true);
            input = TrimPathQuotes(input);
            if (!string.IsNullOrEmpty(input) && File.Exists(input)) { payloadDumperPath = input; SaveConfig(); }
            else if (!string.IsNullOrEmpty(input)) Console.WriteLine("文件不存在，将使用默认配置。");

            Console.Write("请输入 scrcpy 完整路径: ");
            EnableConsolePaste();
            input = Console.ReadLine()?.Trim();
            DisableConsolePaste(true);
            input = TrimPathQuotes(input);
            if (!string.IsNullOrEmpty(input) && File.Exists(input)) { scrcpyPath = input; SaveConfig(); }
            else if (!string.IsNullOrEmpty(input)) Console.WriteLine("文件不存在，将使用默认配置。");
        }

        static bool CheckBootloaderUnlockedInFastboot()
        {
            string output = RunFastbootCommand("getvar unlocked");
            if (!string.IsNullOrEmpty(output) && output.Contains("unlocked: yes")) return true;
            output = RunFastbootCommand("oem device-info");
            if (!string.IsNullOrEmpty(output))
            {
                if (output.Contains("Device unlocked: true") || output.Contains("Device unlocked: yes")) return true;
                if (output.Contains("Device unlocked: false") || output.Contains("Device unlocked: no")) return false;
            }
            Console.Write("无法自动检测 Bootloader 状态，请手动确认是否已解锁？(y/n): ");
            return Console.ReadLine()?.Trim().ToLower() == "y";
        }

        static string RunFastbootCommand(string arguments)
        {
            if (isBetaMode)
            {
                if (arguments.Contains("getvar unlocked") || arguments.Contains("oem device-info"))
                    return "Device unlocked: true";
                if (arguments.Contains("devices"))
                    return "1234567890ABCDEF\tfastboot";
                if (arguments.Contains("getvar all"))
                    return "product: OnePlus 9RT\nversion: 0.4\nsecure: yes";
                return "";
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo { FileName = fastbootPath, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return !string.IsNullOrEmpty(output) ? output : error;
                }
            }
            catch { return ""; }
        }

        static void RunCommand(string command, string arguments)
        {
            if (isBetaMode)
            {
                PrintInfo($"[内测模式] 模拟执行: {command} {arguments}");
                return;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo { FileName = command, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (!string.IsNullOrEmpty(output))
                    {
                        string[] lines = output.Split('\n');
                        foreach (string line in lines)
                        {
                            if (line.Contains("OKAY") || line.Contains("finished") || line.Contains("Writing") || line.Contains("done") || line.Contains("success"))
                            { SetColor(SuccessColor); Console.WriteLine(line); ResetColor(); }
                            else { Console.WriteLine(line); }
                        }
                    }
                    if (!string.IsNullOrEmpty(error)) { SetColor(ErrorColor); Console.WriteLine(error); ResetColor(); }
                }
            }
            catch (Exception ex) { SetColor(ErrorColor); Console.WriteLine($"执行命令时出错: {ex.Message}"); ResetColor(); }
        }
    }
}