using System.Collections.Generic;
using System.Configuration;

namespace UEVR {
    sealed class MainWindowSettings : ApplicationSettingsBase {
        // not implemented
        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("en")]
        public string Language {
            get { return (string)this ["Language"]; }
            set { this ["Language"] = value; }
        }


        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("false")]
        public bool OpenXRRadio {
            get { return (bool)this ["OpenXRRadio"]; }
            set { this ["OpenXRRadio"] = value; }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("true")]
        public bool OpenVRRadio {
            get { return (bool)this ["OpenVRRadio"]; }
            set { this ["OpenVRRadio"] = value; }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("true")]
        public bool NullifyVRPlugins {
            get { return (bool)this ["NullifyVRPlugins"]; }
            set { this ["NullifyVRPlugins"] = value; }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("false")]
        public bool IgnoreFutureVDWarnings {
            get { return (bool)this ["IgnoreFutureVDWarnings"]; }
            set { this ["IgnoreFutureVDWarnings"] = value; }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("true")]
        public bool FocusGameOnInjection {
            get { return (bool)this ["FocusGameOnInjection"]; }
            set { this ["FocusGameOnInjection"] = value; }
        }


        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("false")]
        public bool OpenToTray {
            get { return ((bool)(this ["OpenToTray"])); }
            set { this ["OpenToTray"] = value; }
        }


        // idk I hate this naming but this just makes it skip the tray
        // and directly close when you hit X
        // otherwise it goes to tray
        // minimize button keeps it in the taskbar
        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("false")]
        public bool CloseFromWindow {
            get { return ((bool)(this ["CloseFromWindow"])); }
            set { this ["CloseFromWindow"] = value; }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("false")]
        public bool StartWithWindows {
            get {
                return ((bool)(this ["StartWithWindows"]));
            }
            set {
                this ["StartWithWindows"] = value;
            }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("false")]
        public bool IsMenuOpen {
            get {
                return ((bool)(this ["IsMenuOpen"]));
            }
            set {
                this ["IsMenuOpen"] = value;
            }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("false")]
        public bool VersionSelectorOpen {
            get {
                return ((bool)(this ["VersionSelectorOpen"]));
            }
            set {
                this ["VersionSelectorOpen"] = value;
            }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("false")]
        public bool AutomaticNightlyUpdates {
            get {
                return ((bool)(this ["AutomaticNightlyUpdates"]));
            }
            set {
                this ["AutomaticNightlyUpdates"] = value;
            }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("true")]
        public bool AutomaticInjection {
            get {
                return ((bool)(this ["AutomaticInjection"]));
            }
            set {
                this ["AutomaticInjection"] = value;
            }
        }


        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("true")]
        public bool AutoInjectNewGames {
            get {
                return ((bool)(this ["AutoInjectNewGames"]));
            }
            set {
                this ["AutoInjectNewGames"] = value;
            }
        }


        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("false")]
        public bool IntroducedUpdateFeature {
            get {
                return ((bool)(this ["IntroducedUpdateFeature"]));
            }
            set {
                this ["IntroducedUpdateFeature"] = value;
            }
        }


        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("false")]
        public bool IntroducedAutoInjectFeature {
            get {
                return ((bool)(this ["IntroducedAutoInjectFeature"]));
            }
            set {
                this ["IntroducedAutoInjectFeature"] = value;
            }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("12:00:00")]
        public global::System.TimeSpan UpdateCheckFrequency {
            get {
                return ((global::System.TimeSpan)(this ["UpdateCheckFrequency"]));
            }
            set {
                this ["UpdateCheckFrequency"] = value;
            }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("2026-01-01")]
        public global::System.DateTime LastUpdated {
            get {
                return ((global::System.DateTime)(this ["LastUpdated"]));
            }
            set {
                this ["LastUpdated"] = value;
            }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("false")]
        public bool IgnoreUpdates {
            get {
                return ((bool)(this ["IgnoreUpdates"]));
            }
            set {
                this ["IgnoreUpdates"] = value;
            }
        }

        // Admin only feature enabling early injection
        // for games with profiles or on command line
        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("true")]
        public bool ProcessStartTraceInjection {
            get {
                return ((bool)(this ["ProcessStartTraceInjection"]));
            }
            set {
                this ["ProcessStartTraceInjection"] = value;
            }
        }
        // We really need to update the stable release...
        // I'm adding this setting just to be thorough
        // but I don't think anyone for any reason should use the current stable release
        // you lose half of the lua api...
        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("false")]
        public bool UsingStableVersion {
            get {
                return ((bool)(this ["UsingStableVersion"]));
            }
            set {
                this ["UsingStableVersion"] = value;
            }
        }


        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("")]
        public List<string> BlackListedGames {
            get { return (List<string>)this ["BlackListedGames"]; }
            set { this ["BlackListedGames"] = value; }
        }
        /*
                [UserScopedSettingAttribute()]
                [DefaultSettingValueAttribute("")]
                public List<string> WhiteListedGames
                {
                    get { return ( List<string> )this [ "WhiteListedGames" ]; }
                    set { this [ "WhiteListedGames" ] = value; }
                }*/
        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute(@"<?xml version=""1.0"" encoding=""utf-16""?>
<ArrayOfString xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
 <string>EM-Win64-Shipping</string>
 <string>X6Game-Win64-Shipping</string>
</ArrayOfString>")]
        public System.Collections.Specialized.StringCollection WhiteListedGames {
            get {
                return ((System.Collections.Specialized.StringCollection)(this ["WhiteListedGames"]));
            }
            set {
                this ["WhiteListedGames"] = value;
            }
        }
    }

}

