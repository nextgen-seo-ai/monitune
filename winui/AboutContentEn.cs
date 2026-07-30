namespace MonitorTune;

// English text of the About window. The Russian original lives in AboutContentRu.cs;
// the AboutContent facade picks between them.
internal static class AboutContentEn
{
    public const string ShortPitch =
        "Control the brightness and contrast of external monitors straight from the Windows " +
        "notification area. Sync mode moves several monitors together, brightness and contrast " +
        "can be linked, and night mode can follow a schedule.";

    public const string About =
        "MoniTune is a Windows utility that controls the brightness and contrast of connected " +
        "external monitors, as well as the brightness of laptop built-in displays (eDP), from " +
        "an icon in the notification area. Every monitor gets its own card with independent " +
        "sliders.\n\n" +
        "Control is implemented over the standard DDC/CI interface (Display Data Channel / " +
        "Command Interface) built into most modern monitors. The DDC/CI channel travels along " +
        "the same signal cables as the video itself (HDMI, DisplayPort, DVI or USB-C in DP Alt " +
        "Mode) and requires no additional drivers or hardware.\n\n" +
        "The program does not modify operating system settings, does not touch graphics driver " +
        "parameters and does not require administrator rights. Supported systems are Windows 10 " +
        "version 2004 (20H1, build 19041) and newer, and Windows 11; supported processor " +
        "architectures are x64 and ARM64.";

    public const string HowItWorks =
        "The DDC/CI standard defines a bidirectional service channel between the graphics " +
        "adapter and the monitor. Besides carrying display capabilities (resolution, refresh " +
        "rate, colour space), the channel allows image parameters to be controlled through VCP " +
        "(Virtual Control Panel) commands. MoniTune uses the standard VCP codes: 0x10 for " +
        "brightness and 0x12 for contrast — the same codes the monitor's own on-screen menu " +
        "(OSD) drives.\n\n" +
        "A monitor's microcontroller has limited throughput when processing incoming DDC/CI " +
        "commands. Exceeding the acceptable command rate makes the controller misbehave: on " +
        "some models the on-screen menu stops working, the buttons stop responding and the " +
        "indicators glitch, recoverable only by cutting the monitor's power. To keep things " +
        "stable MoniTune throttles the command rate: consecutive commands to the same monitor " +
        "are at least one second apart. There is no background polling — the DDC/CI bus is " +
        "touched only when you ask for something.";

    public static readonly string[] Features =
    {
        "Independent brightness and contrast control for every connected monitor",
        "Sync mode: one slider movement applies to all monitors at once",
        "Brightness↔contrast link within a single monitor: both parameters move together",
        "Night mode: reduced brightness and contrast on demand or on a schedule, with daytime values restored automatically",
        "Reachable from the notification area without opening extra windows",
        "Rate-limited access to the monitor and no background polling, to keep its controller stable",
        "Several external monitors supported at once, each with its own controls",
        "Works with DDC/CI-capable monitors over HDMI, DisplayPort, DVI and USB-C (DP Alt Mode)",
        "No telemetry and no data collection; the only network request is a periodic update check on github.com (can be disabled in settings)",
        "Open source, no advertising modules and no in-app purchases",
    };

    public const string Privacy =
        "MoniTune does not collect personal data, does not transmit usage statistics and has " +
        "no telemetry. Accounts and registration are not used. All configuration is stored " +
        "locally in your user profile.\n\n" +
        "The program makes a single network connection — a periodic check for new versions in " +
        "the public github.com repository (roughly every 4 hours and when the computer wakes " +
        "from sleep). When an update is found, the corresponding MSIX package is downloaded " +
        "and installed automatically. During such a request your IP address and the standard " +
        "HTTP headers become visible to GitHub under its privacy policy; nothing else — about " +
        "you, your computer or your monitor configuration — is transmitted. Automatic update " +
        "checking can be disabled in the Settings window.\n\n" +
        "If the program crashes, a diagnostic report is saved locally to the LocalCache\\crashes " +
        "folder; reports are never sent automatically.\n\n" +
        "On your command from the tray menu the program can register itself in the Windows " +
        "startup list (StartupTask); it is switched off from the same menu or under " +
        "Settings → Apps → Startup. Update notifications use the Windows AppNotifications " +
        "system service.\n\n" +
        "Hardware interaction is limited to the local DDC/CI bus of connected external " +
        "monitors, the WMI backlight interface of laptop built-in displays (eDP) and standard " +
        "Windows API calls that enumerate display devices. Compliance with this statement can " +
        "be verified by inspecting network traffic or by reading the source code.";

    public static readonly (string Q, string A)[] Faq =
    {
        ("The monitor is missing from the list, or the slider changes nothing",
         "The monitor must support DDC/CI and the corresponding option must be enabled in its " +
         "on-screen menu (OSD). Vendors name the option differently: DDC/CI, DDC, or something " +
         "in a service section. If there is no such option at all, the monitor does not support " +
         "software control and commands cannot be processed."),
        ("Are HDMI, DisplayPort and USB-C connections supported",
         "DDC/CI is supported by every major digital video interface: HDMI, DisplayPort, DVI " +
         "and USB-C in DP Alt Mode. How reliably it works depends on the particular monitor " +
         "model and on the quality of the signal cable."),
        ("Does it work with a laptop's built-in display",
         "Yes. Laptop built-in displays (eDP) have no physical DDC/CI channel, so MoniTune " +
         "controls their brightness through the WMI interface (WmiMonitorBrightness). The WMI " +
         "standard has no contrast control for built-in displays — only brightness is " +
         "available. External monitors use DDC/CI over HDMI / DisplayPort / USB-C with " +
         "independent brightness and contrast."),
        ("Does the program collect data or talk to the network",
         "There is no telemetry and no analytics. The program makes one network request — a " +
         "periodic check for new versions in the public github.com repository (roughly every 4 " +
         "hours and when waking from sleep). When an update is found the MSIX package is " +
         "downloaded and installed automatically. Nothing about you, your computer or your " +
         "monitors is sent. Update checking can be disabled in the Settings window."),
        ("Do the values survive a reboot",
         "Brightness and contrast are stored in the monitor's own non-volatile memory and " +
         "survive a power cycle. MoniTune only sends commands when you act; it does not restore " +
         "values at startup, to avoid loading the monitor's controller unnecessarily."),
        ("Why does the slider react with a delay",
         "The delay comes from the command rate limit in use. Consecutive commands to the same " +
         "monitor are at least one second apart, which lets the monitor's microcontroller " +
         "process them correctly and prevents it from misbehaving."),
        ("What is night mode and how does it work",
         "Night mode moves all connected monitors to reduced brightness and contrast after " +
         "saving the current daytime values. Switching it off restores the original settings. " +
         "The night values and the automatic schedule are configured in the Settings window."),
        ("What is the point of sync mode",
         "With sync mode on, moving the brightness or contrast slider of any monitor applies to " +
         "all the others at the same time. It makes adjusting several displays to changing " +
         "light much quicker."),
    };

    public const string LicenseTitle = "MoniTune end user licence agreement";

    public const string LicenseText =
        "End user licence agreement (EULA) for the MoniTune program\n\n" +
        "Revision date: 30 July 2026.\n\n" +
        "1. Parties and subject\n" +
        "This agreement is concluded between the User and the author of the MoniTune program " +
        "(the Author). Installing, copying or using MoniTune constitutes the User's acceptance " +
        "of the terms of this agreement. If the User disagrees with any of its provisions, use " +
        "of the program is not permitted.\n\n" +
        "2. Purpose of the program\n" +
        "MoniTune is a free Windows utility that controls the brightness and contrast of " +
        "connected external monitors through an icon in the notification area. The program uses " +
        "the standard DDC/CI interface supported by most modern monitors.\n\n" +
        "3. Rights granted\n" +
        "The Author grants the User a free, non-exclusive right, valid in any jurisdiction, to:\n" +
        "— install and use the program on an unlimited number of devices, for personal and " +
        "commercial purposes;\n" +
        "— reproduce the program and pass it to third parties free of charge;\n" +
        "— study and modify the source code and distribute modified versions of the program, " +
        "provided attribution to the original Author and the text of this agreement are " +
        "preserved.\n" +
        "The program is distributed under terms equivalent to the MIT licence.\n\n" +
        "4. Disclaimer of warranties\n" +
        "The program is provided “AS IS”, without any express or implied warranties. The Author " +
        "does not warrant the absence of errors, compatibility with particular monitor models " +
        "or operating system versions, or fitness of the program for the User's particular " +
        "purposes.\n\n" +
        "Noted separately: DDC/CI implementations differ between monitor models. On certain " +
        "models the built-in controller (MCU) may behave unreliably when processing commands. " +
        "MoniTune includes a number of protective measures: no background polling and a " +
        "mandatory interval of at least one second between consecutive commands. These measures " +
        "reduce, but do not entirely eliminate, the behaviour peculiar to specific hardware.\n\n" +
        "5. Limitation of liability\n" +
        "To the maximum extent permitted by applicable law, the Author bears no liability for " +
        "any direct or indirect damages, loss of data, damage to equipment, lost profit or any " +
        "other adverse consequences connected with installing, using or being unable to use the " +
        "program. Use of MoniTune is at the User's own risk.\n\n" +
        "6. Privacy and data handling\n" +
        "MoniTune does not collect personal data, does not transmit usage statistics and does " +
        "not pass information about the User's computer or monitors to third parties. Accounts, " +
        "registration and activation are not provided for.\n\n" +
        "The program makes a single network connection — a periodic check for and download of " +
        "updates from the project's public repository on github.com. Update checking can be " +
        "disabled in the Settings window. If the program crashes, a diagnostic report is " +
        "created locally (the LocalCache\\crashes folder); reports are never submitted " +
        "automatically.\n\n" +
        "At the User's initiative the program may register itself in the Windows startup list " +
        "and use the Windows notification service for update alerts.\n\n" +
        "7. System requirements\n" +
        "Supported operating systems: Windows 10 version 2004 (20H1, build 19041) and newer, " +
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
        "The agreement is in force for the entire period the program is used. The User may " +
        "terminate it at any moment by removing MoniTune from the devices in use. The " +
        "provisions of this agreement concerning disclaimer of warranties, limitation of " +
        "liability and trademark protection survive the end of use.\n\n" +
        "11. Governing law and contact information\n" +
        "General principles of law apply to the relations between the parties, insofar as they " +
        "do not conflict with Microsoft Store rules, except where mandatory provisions of " +
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
        "licence (CC-BY-4.0), https://creativecommons.org/licenses/by/4.0/";
}
