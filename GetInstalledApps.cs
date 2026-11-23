using Microsoft.Win32;
using System;

using namespace AppTrace
{

    public void GetInstalledApps()
    {
        string uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(uninstallKey))
        {
            foreach (string skName in rk.GetSubKeyNames())
            {
                using (RegistryKey sk = rk.OpenSubKey(skName))
                {
                    try
                    {
                        lstInstalled.Items.Add(sk.GetValue("DisplayName"));
                    }
                    catch (Exception ex)
                    {

                    }
                }
            }
        }
        for each(var item in lstInstalled.Items)
        {
            Console.WriteLine(item);
        }
    }
}
