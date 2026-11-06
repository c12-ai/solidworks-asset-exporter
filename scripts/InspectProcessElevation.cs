using System;
using System.Runtime.InteropServices;
class InspectProcessElevation
{
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int id);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int kind,out int value,int size,out int returned);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static int Main(string[] args)
    {
        foreach(var arg in args){var process=OpenProcess(0x1000,false,int.Parse(arg));IntPtr token=IntPtr.Zero;
            try{if(process==IntPtr.Zero||!OpenProcessToken(process,8,out token))throw new Exception("Cannot read token: "+Marshal.GetLastWin32Error());
                int value,size;if(!GetTokenInformation(token,20,out value,4,out size))throw new Exception("Cannot read elevation");Console.WriteLine("PID="+arg+" ELEVATED="+(value!=0));}
            finally{if(token!=IntPtr.Zero)CloseHandle(token);if(process!=IntPtr.Zero)CloseHandle(process);}}
        return 0;
    }
}
