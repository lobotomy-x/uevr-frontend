using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace UEVR {
    class Injector {

        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        public static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        public static string ResolvePath(string dllPath)
        {
            var fname = Path.GetFileName(dllPath);
            var resolvedPath = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnrealVRMod", "UEVR", fname));
            try
            {
                var localDll = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, Path.GetFileName(dllPath));
                if (File.Exists(localDll)) 
                     return Path.GetFullPath(localDll);
            }
            catch
            { }
            return resolvedPath;
        }

     
        // Inject the DLL into the target process
        // dllPath is local filename, relative to EXE.
        public static bool InjectDll(uint processId, string dllPath, out IntPtr dllBase) {
           
            if (Directory.GetCurrentDirectory().EndsWith("UnrealVRMod", StringComparison.OrdinalIgnoreCase))
                Directory.SetCurrentDirectory("UEVR");
            var resolvedPath = Path.Combine(Directory.GetCurrentDirectory(), dllPath);

            dllBase = IntPtr.Zero;

            // Open the target process with the necessary access
            IntPtr processHandle = OpenProcess(ProcessAccessFlags.DEFAULT, false, processId);
            // Get the address of the LoadLibrary function
            IntPtr loadLibraryAddress = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");

            //Allocate memory in the target process for the DLL path
            IntPtr dllPathAddress = VirtualAllocEx(processHandle, IntPtr.Zero, (uint)resolvedPath.Length, 4096U, 64U);
            // Write the DLL path in UTF-16
            int bytesWritten = 0;

            var bytes = Encoding.Unicode.GetBytes(resolvedPath);
            WriteProcessMemory(processHandle, dllPathAddress, bytes, (uint)(resolvedPath.Length * 2), out bytesWritten);
            // Create a remote thread in the target process that calls LoadLibrary with the DLL path
            IntPtr threadHandle = CreateRemoteThread(processHandle, IntPtr.Zero, 0, loadLibraryAddress, dllPathAddress, 0, IntPtr.Zero);

            var u = WaitForSingleObject(threadHandle, 1000);
            return true;
        }

        [Flags]
        public enum ProcessAccessFlags : uint {
            SYNCHRONIZE = 0x100000,
            QUERY_INFORMATION = 0x0400,
            VM_READ = 0x0010,
            ALL_ACCESS = 0x1FFFFF,
            TERMINATE = 0x0001,
            CREATE_THREAD = 0x0002,
            VM_OPERATION = 0x0008,
            VM_WRITE = 0x0020,
            DUP_HANDLE = 0x0040,
            CREATE_PROCESS = 0x0080,
            SET_QUOTA = 0x0100,
            SET_INFORMATION = 0x0200,
            SUSPEND_RESUME = 0x0800,
            QUERY_LIMITED_INFORMATION = 0x1000,
            SET_LIMITED_INFORMATION = 0x2000,
            DEFAULT = 0x1F0FFF,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(
            ProcessAccessFlags access,
            bool inheritHandle,
            uint processId);
        public async static void InjectDllAsync(uint processId, string dllPath) {
            IntPtr dummy;
            bool injected = InjectDll(processId, dllPath, out dummy);
        }
        public static bool InjectDll(uint processId, string dllPath)
        {
            IntPtr dummy;
            InjectDll(processId, dllPath, out dummy);
            try {
                Process? p = Process.GetProcessById((int)processId);
                if (p is not null && !p.HasExited) {
                    foreach(var module in p.Modules) {
                        if (((ProcessModule)module).FileName!.EndsWith(dllPath)) {
                            return true;
                        }
                    }
                }
            } catch { }
            return false;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        // FreeLibrary
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool FreeLibrary(IntPtr hModule);

        public static bool CallFunctionNoArgs(uint processId, string dllPath, IntPtr dllBase, string functionName, bool wait = false) { 
            IntPtr processHandle = OpenProcess(0x1F0FFF, false, processId);

            if (processHandle == IntPtr.Zero)
            {
                MessageBox.Show("Could not open a handle to the target process.\nYou may need to start this program as an administrator, or the process may be protected.");
                return false;
            }

            // We need to load the DLL into our own process temporarily as a workaround for GetProcAddress not working with remote DLLs
            IntPtr localDllHandle = LoadLibrary(dllPath);

            if (localDllHandle == IntPtr.Zero)
            {
                MessageBox.Show("Could not load the target DLL into our own process.");
                return false;
            }

            IntPtr localVa = GetProcAddress(localDllHandle, functionName);

            if (localVa == IntPtr.Zero)
            {
                MessageBox.Show("Could not obtain " + functionName + " address in our own process.");
                return false;
            }

            IntPtr rva = (IntPtr)(localVa.ToInt64() - localDllHandle.ToInt64());
            IntPtr functionAddress = (IntPtr)(dllBase.ToInt64() + rva.ToInt64());

            // Create a remote thread in the target process that calls the function
            IntPtr threadHandle = CreateRemoteThread(processHandle, IntPtr.Zero, 0, functionAddress, IntPtr.Zero, 0, IntPtr.Zero);

            if (threadHandle == IntPtr.Zero)
            {
                MessageBox.Show("Failed to create remote thread in the target processs.");
                return false;
            }

            if (wait)
            {
                WaitForSingleObject(threadHandle, 2000);
            }

            return true;
        }
    }
}