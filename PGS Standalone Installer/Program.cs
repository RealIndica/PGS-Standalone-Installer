using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Net;
using SharpAdbClient;
using SharpAdbClient.DeviceCommands;
using SharpAdbClient.Exceptions;
using SharpAdbClient.Logs;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using ShellProgressBar;
using HtmlAgilityPack;
using System.Linq;

namespace PGS_Standalone_Installer
{
    class Program
    {
        private static string dataDirectory = Environment.CurrentDirectory + "\\data\\";
        private static string tempDirectory = dataDirectory + "tmp\\";
        private static string decompiledAPKDirectory = tempDirectory + "decomp\\";
        private static string resourcesDirectory = dataDirectory + "resources\\";

        private static string adbExecutable = dataDirectory + "platform-tools\\adb.exe";
        private static string apktoolExecutable = dataDirectory + "apktool.jar";
        private static string apksignerExecutable = dataDirectory + "apksigner.jar";

        private static string signCert = resourcesDirectory + "cert.pem";
        private static string signKey = resourcesDirectory + "key.pk8";

        private static string downloadedApk;
        private static string downloadedApkName;

        private static string hostURL = "https://www.pgsharp.com/";
        private static string downloadURL = hostURL + "download";
        private static string betaDownloadURL;

        private static WebClient webClient;

        private static AdbServer server;
        private static AdbClient client;

        private static DeviceData targetDevice;

        private static StartServerResult startADBServer()
        {
            server = new AdbServer();
            StartServerResult result = server.StartServer(adbExecutable, restartServerIfNewer: false);
            return result;
        }

        private static bool CheckADBDevices()
        {
            client = new AdbClient();
            List<DeviceData> devices = client.GetDevices();
            bool ret = false;

            if (devices.Count != 0)
            {
                targetDevice = devices[0];
                Console.WriteLine("Connected to : " + targetDevice.Name + " [" + targetDevice.Model + "] ");
                ret = true;
            } 
            else
            {
                Console.WriteLine("No devices connected!");
                ret = false;
            }

            return ret;
        }

        private static void killADBService()
        {
            Process p = Process.GetProcessesByName("adb")[0];
            p.Kill();
            Console.Write("Killed ADB Server!");
        }

        private static bool isWindows()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public static string GetFinalRedirect(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            int maxRedirCount = 8;  // prevent infinite loops
            string newUrl = url;
            do
            {
                HttpWebRequest req = null;
                HttpWebResponse resp = null;
                try
                {
                    req = (HttpWebRequest)HttpWebRequest.Create(url);
                    req.Method = "HEAD";
                    req.AllowAutoRedirect = false;
                    resp = (HttpWebResponse)req.GetResponse();
                    switch (resp.StatusCode)
                    {
                        case HttpStatusCode.OK:
                            return newUrl;
                        case HttpStatusCode.Redirect:
                        case HttpStatusCode.MovedPermanently:
                        case HttpStatusCode.RedirectKeepVerb:
                        case HttpStatusCode.RedirectMethod:
                            newUrl = resp.Headers["Location"];
                            if (newUrl == null)
                                return url;

                            if (newUrl.IndexOf("://", System.StringComparison.Ordinal) == -1)
                            {
                                // Doesn't have a URL Schema, meaning it's a relative or absolute URL
                                Uri u = new Uri(new Uri(url), newUrl);
                                newUrl = u.ToString();
                            }
                            break;
                        default:
                            return newUrl;
                    }
                    url = newUrl;
                }
                catch (WebException)
                {
                    // Return the last known good URL
                    return newUrl;
                }
                catch (Exception)
                {
                    return null;
                }
                finally
                {
                    if (resp != null)
                        resp.Close();
                }
            } while (maxRedirCount-- > 0);

            return newUrl;
        }

        private static string getBetaURL()
        {
            HtmlWeb web = new HtmlWeb();
            HtmlDocument doc = web.Load(hostURL);

            string url = "";
            foreach (HtmlNode link in doc.DocumentNode.SelectNodes("//a[@href]"))
            {
                string hrefValue = link.GetAttributeValue("href", string.Empty);
                if (hrefValue.EndsWith(".apk"))
                {
                    url = hrefValue;
                    break;
                }
            }

            return url;
        }

        private static string GetFilenameFromWebServer(string url)
        {
            string ret = "";
            ret = url.Substring(url.LastIndexOf('/') + 1);
            return ret;
        }

        private static async Task downloadFile(string URL, string fileName)
        {
            if (File.Exists(fileName))
            {
                File.Delete(fileName);
            }

            string finalURL = GetFinalRedirect(URL);

            if (string.IsNullOrEmpty(finalURL))
            {
                finalURL = URL;
            }

            Console.WriteLine("Got URL : " + finalURL + "\r\n");


            Stopwatch sw = new Stopwatch();
            sw.Start();

            using (ProgressBar pbar = new ProgressBar(100, "0.00 kb/s"))
            {
                pbar.ForegroundColor = ConsoleColor.White;
                webClient.DownloadProgressChanged += (s, e) =>
                {                   
                    IProgress<float> prog = pbar.AsProgress<float>();                
                    prog.Report((float)e.ProgressPercentage / 100);
                    pbar.Message = string.Format("{0} kb/s", (e.BytesReceived / 1024d / sw.Elapsed.TotalSeconds).ToString("0.00"));
                };
                await webClient.DownloadFileTaskAsync(new Uri(finalURL), fileName);
            }
            sw.Stop();
            sw.Reset();
        }

        private static async Task InstallStandard()
        {
            Console.WriteLine("Downloading PGSharp Standard . . .");
            downloadedApkName = GetFilenameFromWebServer(GetFinalRedirect(downloadURL));
            downloadedApk = tempDirectory + downloadedApkName;
            await downloadFile(downloadURL, downloadedApk);
            Console.Clear();
        }

        private static async Task InstallBeta()
        {
            Console.WriteLine("Downloading PGSharp Beta . . .");
            downloadedApkName = GetFilenameFromWebServer(GetFinalRedirect(betaDownloadURL));
            downloadedApk = tempDirectory + downloadedApkName;
            await downloadFile(betaDownloadURL, downloadedApk);
            Console.Clear();
        }

        private static void manageAPK(bool compile)
        {
            string APKEXEC = apktoolExecutable.Replace("\\", "//");
            string DOWNLOADED = downloadedApk.Replace("\\", "//");
            string DECOMP = decompiledAPKDirectory.Replace("\\", "//");
            string DATA = dataDirectory.Replace("\\", "//");

            Process process = new Process();
            process.StartInfo.FileName = "java";

            if (!compile)
            {
                process.StartInfo.Arguments = "-jar \"" + APKEXEC + "\" d \"" + DOWNLOADED + "\" -o \"" + DECOMP + "\"";
            } 
            else
            {
                string tmpAPK = downloadedApkName.Replace(".apk", "");
                if (File.Exists(dataDirectory + tmpAPK + "_standalone.apk"))
                {
                    File.Delete(dataDirectory + tmpAPK + "_standalone.apk");
                }
                process.StartInfo.Arguments = "-jar \"" + APKEXEC + "\" b \"" + DECOMP + "\" -o \"" + DATA + tmpAPK + "_standalone.apk\"";
            }

            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.OutputDataReceived += new DataReceivedEventHandler(OutputHandler);
            process.ErrorDataReceived += new DataReceivedEventHandler(OutputHandler);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            Console.Clear();
        }

        private static void signAPK()
        {
            string APKSIGN = apksignerExecutable.Replace("\\", "//");
            string DATA = dataDirectory.Replace("\\", "//");
            string tmpAPK = downloadedApkName.Replace(".apk", "");
            string tmpKey = signKey.Replace("\\", "//");
            string tmpCert = signCert.Replace("\\", "//");

            Process process = new Process();
            process.StartInfo.FileName = "java";
            process.StartInfo.Arguments = "-jar \"" + APKSIGN + "\" sign --key \"" + tmpKey + "\" --cert \"" + tmpCert + "\" --v2-signing-enabled true --v3-signing-enabled true --v4-signing-enabled false --out \"" + DATA + tmpAPK + "_standalone.apk\" \"" + DATA + tmpAPK + "_standalone.apk\"";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.OutputDataReceived += new DataReceivedEventHandler(OutputHandler);
            process.ErrorDataReceived += new DataReceivedEventHandler(OutputHandler);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            Console.Clear();
        }

        private static void OutputHandler(object sendingProcess, DataReceivedEventArgs outLine)
        {
            Console.WriteLine(outLine.Data);
        }

        public static int CountStringOccurrences(string text, string pattern)
        {
            int count = 0;
            int i = 0;
            while ((i = text.IndexOf(pattern, i)) != -1)
            {
                i += pattern.Length;
                count++;
            }
            return count;
        }

        private static void replaceStringsInFile(string file, string oldstring, string newstring)
        {
            string fileContent = File.ReadAllText(file);
            string updated = fileContent.Replace(oldstring, newstring);
            File.WriteAllText(file, updated);
            int stringReplacements = CountStringOccurrences(updated, newstring);
            Console.WriteLine("Replaced [" + stringReplacements.ToString() + "] strings in " + file);
            System.Threading.Thread.Sleep(100);
        }

        private static void resourcesToDirectory(string resourceFile, string targetDirectory)
        {
            File.Copy(resourcesDirectory + resourceFile, targetDirectory + resourceFile);
            Console.WriteLine("Copied " + resourceFile + " to " + targetDirectory);
            System.Threading.Thread.Sleep(100);
        }

        private static void resourcesToReplace(string resourceFile, string targetFile)
        {
            File.Delete(targetFile);
            File.Copy(resourcesDirectory + resourceFile, targetFile);
            Console.WriteLine("Updated " + targetFile);
            System.Threading.Thread.Sleep(100);
        }

        private static void applyPatches()
        {
            string oldPackageName = "com.nianticlabs.pokemongo";
            string newPackageName = "com.nianticlabs.pokemongo.ares";

            replaceStringsInFile(decompiledAPKDirectory + "apktool.yml", "renameManifestPackage: null", "renameManifestPackage: " + newPackageName);
            replaceStringsInFile(decompiledAPKDirectory + "AndroidManifest.xml", oldPackageName, newPackageName);
            replaceStringsInFile(decompiledAPKDirectory + "AndroidManifest.xml", "<application ", "<application android:networkSecurityConfig=\"@xml/network_security_config\" ");
            replaceStringsInFile(decompiledAPKDirectory + "res\\values\\strings.xml", "<string name=\"notification_title\">Pokémon GO</string>", "<string name=\"notification_title\">PG Sharp</string>");
            replaceStringsInFile(decompiledAPKDirectory + "res\\values\\strings.xml", "<string name=\"app_name\">Pokémon GO</string>", "<string name=\"app_name\">PG Sharp</string>");
            resourcesToDirectory("network_security_config.xml", decompiledAPKDirectory + "res\\xml\\");

            string[] iconDirs = Directory.GetDirectories(decompiledAPKDirectory + "res\\");

            for (int i = 0; i < iconDirs.Length; i++)
            {
                string dirName = new DirectoryInfo(iconDirs[i]).Name;
                if (dirName.StartsWith("mipmap-") && !dirName.EndsWith("v26"))
                {
                    resourcesToReplace("app_icon.png", iconDirs[i] + "\\app_icon.png");
                    resourcesToReplace("ic_launcher_foreground.png", iconDirs[i] + "\\ic_launcher_foreground.png");
                }
            }
            Console.Clear();
        }

        private static void InstallToDevice()
        {
            Console.Clear();
            string tmpAPK = downloadedApkName.Replace(".apk", "");
            Console.WriteLine("Performing Streamed Install . . .");
            PackageManager man = new PackageManager(client, targetDevice);
            man.InstallPackage(dataDirectory + tmpAPK + "_standalone.apk", false);
            Console.Write("Finished Installation!");
            System.Threading.Thread.Sleep(1000);
        }

        private static void FinalizeInstall()
        {
            string tmpAPK = downloadedApkName.Replace(".apk", "");
        START:
            Console.Clear();
            Console.WriteLine("The standalone version of PG Sharp is ready to be installed!\r\nWould you like to install it now? [y/n]");
            ConsoleKeyInfo result = Console.ReadKey();
            switch (result.Key)
            {
                case ConsoleKey.Y:
                    InstallToDevice();
                    break;
                case ConsoleKey.N:
                    Console.Clear();
                    Console.WriteLine("The APK is located in '" + dataDirectory + tmpAPK + "_standalone.apk'.\r\nYou can install it when you are ready.\r\n\r\nPress any key to exit.");                   
                    break;
                default:
                    goto START;
            }
            Console.Clear();
        }

        private static void CleanUp()
        {
            File.Delete(downloadedApk);
            DirectoryInfo dir = new DirectoryInfo(decompiledAPKDirectory);
            dir.Delete(true);
            Console.Clear();
        }

        private static async Task InstallerStart()
        {
        START:
            Console.Clear();
            Console.WriteLine("Please make sure you don't have the Samsung store version of PoGo installed before proceeding.");
            Console.WriteLine("Would you like to install [1]Standard or [2]Beta version?");
            ConsoleKeyInfo result = Console.ReadKey();
            switch (result.Key)
            {
                case ConsoleKey.D1:
                    Console.Clear();
                    await InstallStandard();
                    break;
                case ConsoleKey.D2:
                    Console.Clear();
                    await InstallBeta();
                    break;
                default:
                    goto START;
            }

            Console.WriteLine("Decompiling Original APK . . .");
            manageAPK(false);
            Console.WriteLine("Patching Files . . .");
            applyPatches();
            Console.WriteLine("Recompiling  APK . . .");
            manageAPK(true);
            Console.WriteLine("Signing APK . . .");
            signAPK();
            FinalizeInstall();
            Console.WriteLine("Cleaning up . . .");
            CleanUp();
        }

        private static async Task MainAsync(string[] args)
        {
            Console.Title = "PGS Standalone Installer";

            if (!isWindows())
            {
                Console.WriteLine("This program is untested on your operating system!");
            }

            webClient = new WebClient();
            betaDownloadURL = getBetaURL();

            Console.WriteLine("Starting ADB Server . . .");

            StartServerResult serverResult = startADBServer();
            switch (serverResult)
            {
                case StartServerResult.Started:
                    Console.WriteLine("Started Server!");
                    break;
                case StartServerResult.AlreadyRunning:
                    Console.WriteLine("Server already running!");
                    break;
                case StartServerResult.RestartedOutdatedDaemon:
                    Console.WriteLine("Restarted outdated daemon!");
                    break;
            }

            if (CheckADBDevices())
            {
                await InstallerStart();
            }
            Console.WriteLine("Press Any Key to Exit.");
            Console.ReadKey();
            killADBService();
        }

        static void Main(string[] args)
        {
            MainAsync(args).Wait();
        }
    }
}
