using Microsoft.Win32;

namespace Upshift.Core.Services;

public enum SmartAppControlState { Off, On, Evaluation, Unknown }

/// <summary>
/// Windows' Smart App Control setting, read only (Upshift never changes it). When it's on, Windows can block unsigned
/// programs and DLLs, such as HelixSR's helper program and DLL. The state is the documented
/// VerifiedAndReputablePolicyState value: 0 off, 1 on, 2 evaluation mode.
/// </summary>
public static class SmartAppControl
{
    public static SmartAppControlState Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
            return key?.GetValue("VerifiedAndReputablePolicyState") switch
            {
                0 => SmartAppControlState.Off,
                1 => SmartAppControlState.On,
                2 => SmartAppControlState.Evaluation,
                _ => SmartAppControlState.Unknown
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return SmartAppControlState.Unknown; }
    }
}
