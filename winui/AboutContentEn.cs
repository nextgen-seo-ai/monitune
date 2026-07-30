namespace MonitorTune;

// English text of the About window. The Russian original lives in AboutContentRu.cs;
// the AboutContent facade picks between them.
internal static class AboutContentEn
{
    public const string ShortPitch =
        "Control the brightness and contrast of external monitors straight from the Windows " +
        "notification area. Sync mode adjusts several monitors at once, brightness and contrast " +
        "can be linked, and night mode can follow a schedule.";

    public const string About =
        "MoniTune is a Windows utility that controls the brightness and contrast of connected " +
        "external monitors, as well as the brightness of built-in laptop displays (eDP), from " +
        "an icon in the notification area. Every monitor gets its own card with independent " +
        "sliders.\n\n" +
        "Control uses the standard DDC/CI interface (Display Data Channel " +
        "Command Interface) built into most modern monitors. The DDC/CI channel travels along " +
        "the same signal cables as the video itself (HDMI, DisplayPort, DVI or USB-C in DP Alt " +
        "Mode) and requires no additional drivers or hardware.\n\n" +
        "The program does not change operating system settings, does not alter graphics driver " +
        "parameters and does not require administrator rights. Supported systems are Windows 10 " +
        "version 2004 (20H1, build 19041) or newer, and Windows 11; supported processor " +
        "architectures are x64 and ARM64.";

    public const string HowItWorks =
        "The DDC/CI standard defines a bidirectional service channel between the graphics " +
        "adapter and the monitor. Besides carrying display capabilities (resolution, refresh " +
        "rate, color space), the channel allows image parameters to be controlled through VCP " +
        "(Virtual Control Panel) commands. MoniTune uses the standard VCP codes: 0x10 for " +
        "brightness and 0x12 for contrast — the same codes the monitor's own on-screen display " +
        "(OSD) menu uses.\n\n" +
        "A monitor's microcontroller has limited throughput when processing incoming DDC/CI " +
        "commands. Exceeding the acceptable command rate makes the controller misbehave: on " +
        "some models the on-screen display stops working, the buttons stop responding and the " +
        "indicators misbehave — a state that clears only after the monitor's power is cut. To keep things " +
        "stable, MoniTune throttles the command rate: consecutive commands to the same monitor " +
        "are at least one second apart. There is no background polling — the DDC/CI bus is " +
        "accessed only when you change a setting.";

    public static readonly string[] Features =
    {
        "Independent brightness and contrast control for every connected monitor",
        "Sync mode: one slider movement applies to all monitors at once",
        "Brightness↔contrast link within a single monitor: both parameters move together",
        "Night mode: reduced brightness and contrast on demand or on a schedule, with daytime values restored automatically",
        "Accessible from the notification area without opening extra windows",
        "Rate-limited access to the monitor and no background polling, to keep its controller stable",
        "Several external monitors supported at once, each with its own controls",
        "Works with DDC/CI-capable monitors over HDMI, DisplayPort, DVI and USB-C (DP Alt Mode)",
        "No telemetry and no data collection; the only network request is a periodic update check on github.com (can be disabled in settings)",
        "Open source, with no ads and no in-app purchases",
    };

    public const string Privacy =
        "MoniTune does not collect personal data, does not transmit usage statistics and has " +
        "no telemetry. It does not use accounts or registration. All configuration is stored " +
        "locally in your user profile.\n\n" +
        "The program makes a single network connection — a periodic check for new versions in " +
        "the public github.com repository (roughly every 4 hours and when the computer wakes " +
        "from sleep). When an update is found, the corresponding MSIX package is downloaded " +
        "and installed automatically. During that request your IP address and standard " +
        "HTTP headers are visible to GitHub and are subject to its privacy policy; nothing else — about " +
        "you, your computer or your monitor configuration — is transmitted. Automatic update " +
        "checking can be disabled in the Settings window.\n\n" +
        "If the program crashes, a diagnostic report is saved locally to the LocalCache\\crashes " +
        "folder; reports are never sent automatically.\n\n" +
        "At your request from the tray menu, the program can add itself to the Windows " +
        "startup list (StartupTask); you can turn it off from the same menu or under " +
        "Settings → Apps → Startup. Update notifications use the Windows AppNotifications " +
        "system service.\n\n" +
        "Hardware interaction is limited to the local DDC/CI bus of connected external " +
        "monitors, the WMI backlight interface of built-in laptop displays (eDP) and standard " +
        "Windows API calls that enumerate display devices. You can verify all of this by " +
        "inspecting network traffic or by reading the source code.";

    public static readonly (string Q, string A)[] Faq =
    {
        ("The monitor is missing from the list, or the slider changes nothing",
         "The monitor must support DDC/CI and the corresponding option must be enabled in its " +
         "on-screen display (OSD) menu. Vendors name the option differently: DDC/CI, DDC, or an entry " +
         "in a service menu. If the option is missing entirely, the monitor does not support " +
         "software control and commands cannot be processed."),
        ("Are HDMI, DisplayPort and USB-C connections supported",
         "DDC/CI is supported by every major digital video interface: HDMI, DisplayPort, DVI " +
         "and USB-C in DP Alt Mode. How reliably it works depends on the particular monitor " +
         "model and on the quality of the signal cable."),
        ("Does it work with a laptop's built-in display",
         "Yes. Built-in laptop displays (eDP) have no physical DDC/CI channel, so MoniTune " +
         "controls their brightness through the WMI interface (WmiMonitorBrightness). The WMI " +
         "interface provides no contrast control for built-in displays — only brightness is " +
         "available. External monitors use DDC/CI over HDMI / DisplayPort / USB-C with " +
         "independent brightness and contrast."),
        ("Does the program collect data or connect to the network",
         "There is no telemetry and no analytics. The program makes one network request — a " +
         "periodic check for new versions in the public github.com repository (roughly every 4 " +
         "hours and when waking from sleep). When an update is found the MSIX package is " +
         "downloaded and installed automatically. Nothing about you, your computer or your " +
         "monitors is sent. Update checking can be disabled in the Settings window."),
        ("Do the values survive a reboot",
         "Brightness and contrast are stored in the monitor's own non-volatile memory and " +
         "survive a power cycle. MoniTune sends commands only when you make a change; it does not restore " +
         "values at startup, to avoid putting unnecessary load on the monitor's controller."),
        ("Why does the slider react with a delay",
         "The delay comes from the command rate limit. Consecutive commands to the same " +
         "monitor are at least one second apart, which lets the monitor's microcontroller " +
         "process them correctly and prevents it from misbehaving."),
        ("What is night mode and how does it work",
         "Night mode switches all connected monitors to reduced brightness and contrast after " +
         "saving the current daytime values. Switching it off restores the original settings. " +
         "The night values and the automatic schedule are configured in the Settings window."),
        ("What is sync mode for",
         "With sync mode on, moving the brightness or contrast slider on any monitor applies the same change to " +
         "all the others at the same time. It makes it much quicker to adjust several displays as the " +
         "ambient light changes."),
    };

    public const string LicenseTitle = "MoniTune End User License Agreement";

    public const string LicenseText =
        "End User License Agreement (EULA) for MoniTune\n\n" +
        "Revision date: July 30, 2026.\n\n" +
        "1. Parties and subject matter\n" +
        "This agreement is entered into between the User and the author of MoniTune " +
        "(the Author). Installing, copying or using MoniTune constitutes the User's acceptance " +
        "of the terms of this agreement. If the User does not agree to any of its provisions, use " +
        "of the program is not permitted.\n\n" +
        "2. Purpose of the program\n" +
        "MoniTune is a free Windows utility that controls the brightness and contrast of " +
        "connected external monitors through an icon in the notification area. The program uses " +
        "the standard DDC/CI interface supported by most modern monitors.\n\n" +
        "3. Grant of rights\n" +
        "The Author grants the User a royalty-free, non-exclusive, worldwide right to:\n" +
        "— install and use the program on an unlimited number of devices, for personal and " +
        "commercial purposes;\n" +
        "— reproduce the program and distribute it to third parties at no charge;\n" +
        "— study and modify the source code and distribute modified versions of the program, " +
        "provided that attribution to the original Author and the text of this agreement are " +
        "preserved.\n" +
        "The program is distributed under terms equivalent to the MIT license.\n\n" +
        "4. Disclaimer of warranties\n" +
        "The program is provided “AS IS”, without warranty of any kind, express or implied. The Author " +
        "does not warrant that the program is free of errors, that it is compatible with particular monitor models " +
        "or operating system versions, or that it is fit for the User's particular " +
        "purposes.\n\n" +
        "Note in particular: DDC/CI implementations differ between monitor models. On certain " +
        "models the built-in controller (MCU) may behave unreliably when processing commands. " +
        "MoniTune includes a number of protective measures: no background polling and a " +
        "mandatory interval of at least one second between consecutive commands. These measures " +
        "reduce, but do not entirely eliminate, the risk of hardware-specific misbehavior.\n\n" +
        "5. Limitation of liability\n" +
        "To the maximum extent permitted by applicable law, the Author bears no liability for " +
        "any direct or indirect damages, loss of data, damage to equipment, lost profits or any " +
        "other adverse consequences arising out of or in connection with the installation or use of, or inability to use, the " +
        "program. Use of MoniTune is at the User's own risk.\n\n" +
        "6. Privacy and data handling\n" +
        "MoniTune does not collect personal data, does not transmit usage statistics and does " +
        "not disclose information about the User's computer or monitors to third parties. The program " +
        "does not use accounts, registration or activation.\n\n" +
        "The program makes only one kind of network connection: it periodically checks for and downloads " +
        "updates from the project's public repository on github.com. Update checking can be " +
        "disabled in the Settings window. If the program crashes, a diagnostic report is " +
        "created locally (the LocalCache\\crashes folder); reports are never sent " +
        "automatically.\n\n" +
        "If the User chooses, the program can add itself to the Windows startup list " +
        "and use the Windows notification service for update alerts.\n\n" +
        "7. System requirements\n" +
        "Supported operating systems: Windows 10 version 2004 (20H1, build 19041) or newer, " +
        "and Windows 11. Controlling image parameters requires a monitor with DDC/CI support " +
        "connected over DisplayPort, HDMI, USB-C or DVI.\n\n" +
        "8. Third-party trademarks\n" +
        "The names Windows, Microsoft, Samsung, LG, Dell, ASUS, BenQ, Acer, HP and other names " +
        "and logos mentioned are the property of their respective owners. MoniTune is not " +
        "affiliated with, endorsed by or sponsored by these companies. The names are used " +
        "solely to describe compatibility.\n\n" +
        "9. Updates and changes\n" +
        "The Author may release MoniTune updates through the program's built-in mechanism for " +
        "checking and downloading new versions from the project's public GitHub repository, " +
        "through the Microsoft Store, and by distributing source code. The Author reserves the " +
        "right to change the program's functionality at any time and to suspend or discontinue " +
        "its support and development. The Author may also amend the text of this agreement for " +
        "subsequent versions of the program; an updated revision takes effect upon " +
        "publication.\n\n" +
        "10. Term of the agreement\n" +
        "This agreement remains in force for as long as the program is used. The User may " +
        "terminate it at any time by uninstalling MoniTune from all devices in use. The " +
        "provisions of this agreement concerning disclaimer of warranties, limitation of " +
        "liability and third-party trademarks survive termination.\n\n" +
        "11. Governing law and contact information\n" +
        "The relationship between the parties is governed by general principles of law, to the extent they " +
        "do not conflict with Microsoft Store policies, except where mandatory provisions of " +
        "applicable law state otherwise. For questions related to use of the program, contact " +
        "the Author through the project page in the Microsoft Store or through the source code " +
        "repository.\n\n" +
        "Using MoniTune confirms the User's acceptance of the terms of this agreement.\n\n" +
        "12. Open data used\n" +
        "The program uses the following open data sets to identify the models of connected " +
        "monitors:\n" +
        "— the UEFI Forum PNP ID Registry — publicly available manufacturer data.\n" +
        "— a derived monitor model mapping from the linux-hardware.org project (EDID " +
        "database), distributed under the Creative Commons Attribution 4.0 International " +
        "license (CC-BY-4.0), https://creativecommons.org/licenses/by/4.0/";
}
