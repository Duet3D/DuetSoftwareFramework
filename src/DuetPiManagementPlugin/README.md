# DuetPi Management Plugin 

This plugin provides system management functions using regular RepRapFirmware M-codes for the DuetPi distribution.

## Supported codes

- `M540` Set MAC address
- `M550` Set Name
- `M552` Set IP address, enable/disable network interface
- `M553` Set Netmask
- `M554` Set Gateway
- `M586` Configure network protocols
- `M587` Add WiFi host network to remembered list, or list remembered networks
- `M588` Forget WiFi host network
- `M589` Configure access point parameters
- `M999 B-1` Reboot SBC

## Build instructions

This plugin requires .NET 5 SDK to be installed first.

1. Open a command prompt in this directory
2. Run `dotnet publish -r linux-arm -o .\zip\dsf\ /p:PublishTrimmed=true`
3. Go to the `zip` directory and compress all the files and directories in it to a single ZIP file

To install this plugin, you may have to enable super-user (root) plugins in `/opt/dsf/conf/config.json` first (set `` to `true).
After that you can upload the generated ZIP file using the "Upload & Start" button on DWC and install it.

## Notes and limitations

Unlike in RRF the changes performed by this plugin are permanently saved. This means they should be used **only once** to reconfigure the SBC.
In addition it comes with the following limitations:

- `M586 P2 R` cannot be used to set the Telnet port. If this is required, the file `/etc/inetd.conf` must be manually edited (change `telnet` to a port of your choice).
- `M587` does not save the IP address configuration per SSID. Once set, the configuration is used for every available SSID