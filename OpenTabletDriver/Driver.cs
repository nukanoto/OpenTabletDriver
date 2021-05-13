using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OpenTabletDriver.Devices;
using OpenTabletDriver.Interop;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Devices;
using OpenTabletDriver.Plugin.Tablet;
using OpenTabletDriver.Tablet;

namespace OpenTabletDriver
{
    public abstract class Driver : IDriver, IDisposable
    {
        public event EventHandler<DevicesChangedEventArgs> DevicesChanged;
        public event EventHandler<IEnumerable<TabletReference>> TabletsChanged;

        private static readonly Dictionary<string, Func<IReportParser<IDeviceReport>>> reportParserDict = new Dictionary<string, Func<IReportParser<IDeviceReport>>>
        {
            { typeof(DeviceReportParser).FullName, () => new DeviceReportParser() },
            { typeof(TabletReportParser).FullName, () => new TabletReportParser() },
            { typeof(AuxReportParser).FullName, () => new AuxReportParser() },
            { typeof(TiltTabletReportParser).FullName, () => new TiltTabletReportParser() },
            { typeof(Vendors.SkipByteTabletReportParser).FullName, () => new Vendors.SkipByteTabletReportParser() },
            { typeof(Vendors.UCLogic.UCLogicReportParser).FullName, () => new Vendors.UCLogic.UCLogicReportParser() },
            { typeof(Vendors.Huion.GianoReportParser).FullName, () => new Vendors.Huion.GianoReportParser() },
            { typeof(Vendors.Wacom.BambooReportParser).FullName, () => new Vendors.Wacom.BambooReportParser() },
            { typeof(Vendors.Wacom.IntuosV2ReportParser).FullName, () => new Vendors.Wacom.IntuosV2ReportParser() },
            { typeof(Vendors.Wacom.IntuosV3ReportParser).FullName, () => new Vendors.Wacom.IntuosV3ReportParser() },
            { typeof(Vendors.Wacom.Wacom64bAuxReportParser).FullName, () => new Vendors.Wacom.Wacom64bAuxReportParser() },
            { typeof(Vendors.Wacom.WacomDriverIntuosV2ReportParser).FullName, () => new Vendors.Wacom.WacomDriverIntuosV2ReportParser() },
            { typeof(Vendors.Wacom.WacomDriverIntuosV3ReportParser).FullName, () => new Vendors.Wacom.WacomDriverIntuosV3ReportParser() },
            { typeof(Vendors.XP_Pen.XP_PenReportParser).FullName, () => new Vendors.XP_Pen.XP_PenReportParser() },
            { typeof(Vendors.XP_Pen.XP_PenTiltReportParser).FullName, () => new Vendors.XP_Pen.XP_PenTiltReportParser() }
        };

        public IEnumerable<TabletReference> Tablets => Devices.Select(c => c.CreateReference());
        public IList<InputDeviceTree> Devices { private set; get; } = new List<InputDeviceTree>();

        public virtual bool Detect()
        {
            bool success = false;

            Devices.Clear();
            foreach (var config in GetTabletConfigurations())
            {
                if (Match(config) is InputDeviceTree tree)
                {
                    success = true;
                    Devices.Add(tree);
                }
            }

            TabletsChanged?.Invoke(this, Tablets);

            return success;
        }

        public virtual IReportParser<IDeviceReport> GetReportParser(DeviceIdentifier identifier)
        {
            return reportParserDict[identifier.ReportParser].Invoke();
        }

        protected abstract IEnumerable<TabletConfiguration> GetTabletConfigurations();

        protected virtual InputDeviceTree Match(TabletConfiguration config)
        {
            Log.Write("Detect", $"Searching for tablet '{config.Name}'");
            try
            {
                var devices = new List<InputDevice>();
                if (MatchDevice(config, config.DigitizerIdentifiers) is InputDevice digitizer)
                {
                    Log.Write("Detect", $"Found tablet '{config.Name}'");
                    devices.Add(digitizer);

                    if (config.AuxilaryDeviceIdentifiers.Any())
                    {
                        if (MatchDevice(config, config.AuxilaryDeviceIdentifiers) is InputDevice aux)
                            devices.Add(aux);
                        else
                            Log.Write("Detect", "Failed to find auxiliary device, express keys may be unavailable.", LogLevel.Warning);
                    }

                    return new InputDeviceTree(config, devices);
                }
            }
            catch (IOException iex) when (iex.Message.Contains("Unable to open HID class device")
                && SystemInterop.CurrentPlatform == PluginPlatform.Linux)
            {
                Log.Write("DeviceUnathorizedAccessException",
                    "Current user don't have the permissions to open device streams. "
                    + "To fix this issue, please follow the instructions from https://github.com/OpenTabletDriver/OpenTabletDriver/wiki/Linux-FAQ#the-driver-fails-to-open-the-tablet-deviceioexception", LogLevel.Error);
            }
            catch (ArgumentOutOfRangeException aex) when (aex.Message.Contains("Value range is [0, 15]")
                && SystemInterop.CurrentPlatform == PluginPlatform.Linux)
            {
                Log.Write("DeviceInUseException",
                    "Device is currently in use by another kernel module. "
                    + "To fix this issue, please follow the instructions from https://github.com/OpenTabletDriver/OpenTabletDriver/wiki/Linux-FAQ#argumentoutofrangeexception-value-0-15", LogLevel.Error);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
            return null;
        }

        private InputDevice MatchDevice(TabletConfiguration config, IList<DeviceIdentifier> identifiers)
        {
            foreach (var identifier in identifiers)
            {
                var matches = FindMatchingDigitizer(identifier, config.Attributes);

                if (matches.Count() > 1)
                    Log.Write("Detect", "More than 1 matching device has been found.", LogLevel.Warning);

                foreach (IDeviceEndpoint dev in matches)
                {
                    try
                    {
                        return new InputDevice(this, dev, config, identifier);
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex);
                        continue;
                    }
                }
            }
            return null;
        }

        private IEnumerable<IDeviceEndpoint> FindMatchingDigitizer(DeviceIdentifier identifier, Dictionary<string, string> attributes)
        {
            return from device in FindMatches(identifier)
                where DigitizerMatchesAttribute(device, attributes)
                select device;
        }

        private IEnumerable<IDeviceEndpoint> FindMatchingAuxiliary(DeviceIdentifier identifier, Dictionary<string, string> attributes)
        {
            return from device in FindMatches(identifier)
                where AuxMatchesAttribute(device, attributes)
                select device;
        }

        private IEnumerable<IDeviceEndpoint> FindMatches(DeviceIdentifier identifier)
        {
            return from device in HidSharpDeviceRootHub.Current.GetDevices()
                where identifier.VendorID == device.VendorID
                where identifier.ProductID == device.ProductID
                where device.CanOpen
                where identifier.InputReportLength == null || identifier.InputReportLength == device.InputReportLength
                where identifier.OutputReportLength == null || identifier.OutputReportLength == device.OutputReportLength
                where DeviceMatchesAllStrings(device, identifier)
                select device;
        }

        private bool DigitizerMatchesAttribute(IDeviceEndpoint device, Dictionary<string, string> attributes)
        {
            if (SystemInterop.CurrentPlatform != PluginPlatform.Windows)
                return true;

            var devName = device.DevicePath;

            bool interfaceMatches = attributes.ContainsKey("WinInterface") ? Regex.IsMatch(devName, $"&mi_{attributes["WinInterface"]}") : true;
            bool keyMatches = attributes.ContainsKey("WinUsage") ? Regex.IsMatch(devName, $"&col{attributes["WinUsage"]}") : true;

            return interfaceMatches && keyMatches;
        }

        private bool AuxMatchesAttribute(IDeviceEndpoint device, Dictionary<string, string> attributes)
        {
            return true; // Future proofing
        }

        private bool DeviceMatchesAllStrings(IDeviceEndpoint device, DeviceIdentifier identifier)
        {
            if (identifier.DeviceStrings == null || identifier.DeviceStrings.Count == 0)
                return true;

            foreach (var matchQuery in identifier.DeviceStrings)
            {
                try
                {
                    // Iterate through each device string, if one doesn't match then its the wrong configuration.
                    var input = device.GetDeviceString(matchQuery.Key);
                    var pattern = matchQuery.Value;
                    if (!Regex.IsMatch(input, pattern))
                        return false;
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                    return false;
                }
            }
            return true;
        }

        public void Dispose()
        {
            foreach (InputDeviceTree tree in Devices)
                foreach (InputDevice dev in tree.InputDevices)
                    dev.Dispose();

            Devices = null;
        }
    }
}
