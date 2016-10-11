using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;
using Android.Support.V7.App;
using Android.Text.Method;
using Android.Util;
using Android.Views;
using Android.Widget;
using BmwDeepObd.FilePicker;
using EdiabasLib;

// ReSharper disable CanBeReplacedWithTryCastAndCheckForNull

namespace BmwDeepObd
{
    [Android.App.Activity(Label = "@string/xml_tool_title",
            ConfigurationChanges = Android.Content.PM.ConfigChanges.KeyboardHidden |
                        Android.Content.PM.ConfigChanges.Orientation |
                        Android.Content.PM.ConfigChanges.ScreenSize)]
    public class XmlToolActivity : AppCompatActivity
    {
        private enum ActivityRequest
        {
            RequestSelectSgbd,
            RequestSelectDevice,
            RequestCanAdapterConfig,
            RequestSelectJobs,
            RequestYandexKey,
        }

        public class EcuInfo
        {
            public EcuInfo(string name, Int64 address, string description, string sgbd, string grp, string mwTabFileName = null)
            {
                Name = name;
                Address = address;
                Description = description;
                DescriptionTrans = null;
                Sgbd = sgbd;
                Grp = grp;
                Selected = false;
                Vin = null;
                PageName = name;
                EcuName = name;
                JobList = null;
                MwTabFileName = mwTabFileName;
                MwTabList = null;
                ReadCommand = null;
            }

            public string Name { get; set; }

            public Int64 Address { get; set; }

            public string Description { get; set; }

            public string DescriptionTrans { get; set; }

            public string Sgbd { get; set; }

            public string Grp { get; set; }

            public string Vin { get; set; }

            public bool Selected { get; set; }

            public string PageName { get; set; }

            public string EcuName { get; set; }

            public List<XmlToolEcuActivity.JobInfo> JobList { get; set; }

            public string MwTabFileName { get; set; }

            public List<ActivityCommon.MwTabEntry> MwTabList { get; set; }

            public bool IgnoreXmlFile { get; set; }

            public string ReadCommand { get; set; }
        }

        private const string XmlDocumentFrame =
            @"<?xml version=""1.0"" encoding=""utf-8"" ?>
            <{0} xmlns=""http://www.holeschak.de/BmwDeepObd""
            xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
            xsi:schemaLocation=""http://www.holeschak.de/BmwDeepObd BmwDeepObd.xsd"">
            </{0}>";
        private const string XsdFileName = "BmwDeepObd.xsd";
        private const string TranslationFileName = "Translation.xml";

        private const string PageExtension = ".ccpage";
        private const string ErrorsFileName = "Errors.ccpage";
        private const string PagesFileName = "Pages.ccpages";
        private const string ConfigFileExtension = ".cccfg";
        private const string DisplayNamePage = "!PAGE_NAME";
        private const string DisplayNameJobPrefix = "!JOB#";
        private const string DisplayNameEcuPrefix = "!ECU#";
        private const string ManualConfigName = "Manual";
        private const string UnknownVinConfigName = "Unknown";
        private static readonly string[] EcuFileNames =
        {
            "e60", "e65", "e70", "e81", "e87", "e89X", "e90", "m12", "r56", "f01", "f01bn2k", "rr01"
        };
        private static readonly string[] ReadVinJobs =
        {
            "C_FG_LESEN_FUNKTIONAL", "PROG_FG_NR_LESEN_FUNKTIONAL", "AIF_LESEN_FUNKTIONAL"
        };

        // Intent extra
        public const string ExtraInitDir = "init_dir";
        public const string ExtraAppDataDir = "app_data_dir";
        public const string ExtraInterface = "interface";
        public const string ExtraDeviceName = "device_name";
        public const string ExtraDeviceAddress = "device_address";
        public const string ExtraEnetIp = "enet_ip";
        public const string ExtraFileName = "file_name";
        public static readonly CultureInfo Culture = CultureInfo.CreateSpecificCulture("en");

        public delegate void MwTabFileSelected(string fileName);

        private View _barView;
        private Button _buttonRead;
        private Button _buttonSafe;
        private EcuListAdapter _ecuListAdapter;
        private TextView _textViewCarInfo;
        private string _ecuDir;
        private string _appDataDir;
        private string _deviceName = string.Empty;
        private string _deviceAddress = string.Empty;
        private string _lastFileName = string.Empty;
        private string _datUkdDir = string.Empty;
        private bool _addErrorsPage = true;
        private int _manualConfigIdx;
        private bool _traceActive = true;
        private bool _traceAppend;
        private bool _commErrorsOccured;
        private bool _activityActive;
        private volatile bool _ediabasJobAbort;
        private bool _autoStart;
        private ActivityCommon _activityCommon;
        private EdiabasNet _ediabas;
        private string _traceDir;
        private Thread _jobThread;
        private string _vin = string.Empty;
        private readonly List<EcuInfo> _ecuList = new List<EcuInfo>();
        private bool _ecuListTranslated;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            SupportActionBar.SetHomeButtonEnabled(true);
            SupportActionBar.SetDisplayShowHomeEnabled(true);
            SupportActionBar.SetDisplayHomeAsUpEnabled(true);
            SupportActionBar.SetDisplayShowCustomEnabled(true);
            SetContentView(Resource.Layout.xml_tool);

            _barView = LayoutInflater.Inflate(Resource.Layout.bar_xml_tool, null);
            ActionBar.LayoutParams barLayoutParams = new ActionBar.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent);
            barLayoutParams.Gravity = barLayoutParams.Gravity &
                (int)(~(GravityFlags.HorizontalGravityMask | GravityFlags.VerticalGravityMask)) |
                (int)(GravityFlags.Left | GravityFlags.CenterVertical);
            SupportActionBar.SetCustomView(_barView, barLayoutParams);

            SetResult(Android.App.Result.Canceled);

            _buttonRead = _barView.FindViewById<Button>(Resource.Id.buttonXmlRead);
            _buttonRead.Click += (sender, args) =>
            {
                if (_manualConfigIdx > 0)
                {
                    ShowEditMenu(_buttonRead);
                    return;
                }
                PerformAnalyze();
            };
            _buttonSafe = _barView.FindViewById<Button>(Resource.Id.buttonXmlSafe);
            _buttonSafe.Click += (sender, args) =>
            {
                SaveConfiguration(false);
            };

            _textViewCarInfo = FindViewById<TextView>(Resource.Id.textViewCarInfo);
            ListView listViewEcu = FindViewById<ListView>(Resource.Id.listEcu);
            _ecuListAdapter = new EcuListAdapter(this);
            listViewEcu.Adapter = _ecuListAdapter;
            listViewEcu.ItemClick += (sender, args) =>
            {
                int pos = args.Position;
                if (pos >= 0)
                {
                    ExecuteJobsRead(_ecuList[pos]);
                }
            };
            listViewEcu.ItemLongClick += (sender, args) =>
            {
                ShowContextMenu(args.View, args.Position);
            };

            _activityCommon = new ActivityCommon(this, () =>
            {
                if (_activityActive)
                {
                    SupportInvalidateOptionsMenu();
                    UpdateDisplay();
                }
            }, BroadcastReceived)
            {
                SelectedInterface = (ActivityCommon.InterfaceType)
                    Intent.GetIntExtra(ExtraInterface, (int)ActivityCommon.InterfaceType.None)
            };

            _ecuDir = Intent.GetStringExtra(ExtraInitDir);
            _appDataDir = Intent.GetStringExtra(ExtraAppDataDir);
            _deviceName = Intent.GetStringExtra(ExtraDeviceName);
            _deviceAddress = Intent.GetStringExtra(ExtraDeviceAddress);
            _activityCommon.SelectedEnetIp = Intent.GetStringExtra(ExtraEnetIp);
            _lastFileName = Intent.GetStringExtra(ExtraFileName);
            _datUkdDir = ActivityCommon.GetVagDatUkdDir(_ecuDir);
            string configName = Path.GetFileNameWithoutExtension(_lastFileName);
            if (!string.IsNullOrEmpty(configName) && configName.StartsWith(ManualConfigName))
            {
                try
                {
                    _manualConfigIdx = Convert.ToInt32(configName.Substring(ManualConfigName.Length, 1));
                }
                catch (Exception)
                {
                    _manualConfigIdx = 0;
                }
            }
            if (ActivityCommon.SelectedManufacturer != ActivityCommon.ManufacturerType.Bmw && _manualConfigIdx == 0)
            {
                _manualConfigIdx = 1;
            }

            EdiabasClose();
            if (_manualConfigIdx > 0)
            {
                EdiabasOpen();
                ReadAllXml();
                ExecuteUpdateEcuInfo();
            }
            UpdateDisplay();
        }

        protected override void OnResume()
        {
            base.OnResume();

            _activityActive = true;
            if (!_activityCommon.RequestEnableTranslate((sender, args) =>
            {
                HandleStartDialogs();
            }))
            {
                HandleStartDialogs();
            }
        }

        protected override void OnPause()
        {
            base.OnPause();

            _activityActive = false;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            _ediabasJobAbort = true;
            if (IsJobRunning())
            {
                _jobThread.Join();
            }
            EdiabasClose();
            _activityCommon.Dispose();
        }

        public override void OnBackPressed()
        {
            if (IsJobRunning())
            {
                return;
            }
            if (!_buttonSafe.Enabled)
            {
                OnBackPressedContinue();
                return;
            }
            new AlertDialog.Builder(this)
                .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                {
                    SaveConfiguration(true);
                })
                .SetNegativeButton(Resource.String.button_no, (sender, args) =>
                {
                    OnBackPressedContinue();
                })
                .SetMessage(Resource.String.xml_tool_msg_save_config)
                .SetTitle(Resource.String.alert_title_question)
                .Show();
        }

        private void OnBackPressedContinue()
        {
            if (!SendTraceFile((sender, args) =>
            {
                base.OnBackPressed();
            }))
            {
                base.OnBackPressed();
            }
        }

        protected override void OnActivityResult(int requestCode, Android.App.Result resultCode, Intent data)
        {
            switch ((ActivityRequest)requestCode)
            {
                case ActivityRequest.RequestSelectSgbd:
                    // When FilePickerActivity returns with a file
                    if (data != null && resultCode == Android.App.Result.Ok)
                    {
                        string fileName = data.Extras.GetString(FilePickerActivity.ExtraFileName);
                        if (string.IsNullOrEmpty(fileName))
                        {
                            break;
                        }
                        string ecuName = Path.GetFileNameWithoutExtension(fileName);
                        if (string.IsNullOrEmpty(ecuName))
                        {
                            break;
                        }
                        if (_ecuList.Any(ecuInfo => string.Compare(ecuInfo.Sgbd, ecuName, StringComparison.OrdinalIgnoreCase) == 0))
                        {
                            break;
                        }
                        EcuInfo ecuInfoNew = new EcuInfo(ecuName, -1, string.Empty, ecuName, string.Empty)
                        {
                            PageName = string.Empty,
                            EcuName = string.Empty
                        };
                        _ecuList.Add(ecuInfoNew);
                        ExecuteUpdateEcuInfo();
                        SupportInvalidateOptionsMenu();
                        UpdateDisplay();
                    }
                    break;

                case ActivityRequest.RequestSelectDevice:
                    // When DeviceListActivity returns with a device to connect
                    if (data != null && resultCode == Android.App.Result.Ok)
                    {
                        // Get the device MAC address
                        _deviceName = data.Extras.GetString(DeviceListActivity.ExtraDeviceName);
                        _deviceAddress = data.Extras.GetString(DeviceListActivity.ExtraDeviceAddress);
                        bool callAdapterConfig = data.Extras.GetBoolean(DeviceListActivity.ExtraCallAdapterConfig, false);
                        EdiabasClose();
                        SupportInvalidateOptionsMenu();
                        if (callAdapterConfig)
                        {
                            AdapterConfig();
                        }
                        else if (_autoStart)
                        {
                            ExecuteAnalyzeJob();
                        }
                    }
                    _autoStart = false;
                    break;

                case ActivityRequest.RequestCanAdapterConfig:
                    break;

                case ActivityRequest.RequestSelectJobs:
                    if (XmlToolEcuActivity.IntentEcuInfo.JobList != null)
                    {
                        int selectCount = XmlToolEcuActivity.IntentEcuInfo.JobList.Count(job => job.Selected);
                        XmlToolEcuActivity.IntentEcuInfo.Selected = selectCount > 0;
                        _ecuListAdapter.NotifyDataSetChanged();
                        UpdateDisplay();
                    }
                    break;

                case ActivityRequest.RequestYandexKey:
                    ActivityCommon.EnableTranslation = !string.IsNullOrWhiteSpace(ActivityCommon.YandexApiKey);
                    SupportInvalidateOptionsMenu();
                    UpdateDisplay();
                    break;
            }
        }

        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            var inflater = MenuInflater;
            inflater.Inflate(Resource.Menu.xml_tool_menu, menu);
            return true;
        }

        public override bool OnPrepareOptionsMenu(IMenu menu)
        {
            bool commActive = IsJobRunning();
            bool interfaceAvailable = _activityCommon.IsInterfaceAvailable();

            IMenuItem selInterfaceMenu = menu.FindItem(Resource.Id.menu_tool_sel_interface);
            if (selInterfaceMenu != null)
            {
                selInterfaceMenu.SetTitle(string.Format(Culture, "{0}: {1}", GetString(Resource.String.menu_tool_sel_interface), _activityCommon.InterfaceName()));
                selInterfaceMenu.SetEnabled(!commActive);
            }

            IMenuItem scanMenu = menu.FindItem(Resource.Id.menu_scan);
            if (scanMenu != null)
            {
                scanMenu.SetTitle(string.Format(Culture, "{0}: {1}", GetString(Resource.String.menu_adapter), _deviceName));
                scanMenu.SetEnabled(!commActive && interfaceAvailable);
                scanMenu.SetVisible(_activityCommon.SelectedInterface == ActivityCommon.InterfaceType.Bluetooth);
            }

            IMenuItem adapterConfigMenu = menu.FindItem(Resource.Id.menu_adapter_config);
            if (adapterConfigMenu != null)
            {
                adapterConfigMenu.SetEnabled(interfaceAvailable && !commActive);
                adapterConfigMenu.SetVisible(_activityCommon.AllowAdapterConfig(_deviceAddress));
            }

            IMenuItem enetIpMenu = menu.FindItem(Resource.Id.menu_enet_ip);
            if (enetIpMenu != null)
            {
                enetIpMenu.SetTitle(string.Format(Culture, "{0}: {1}", GetString(Resource.String.menu_enet_ip),
                    string.IsNullOrEmpty(_activityCommon.SelectedEnetIp) ? GetString(Resource.String.select_enet_ip_auto) : _activityCommon.SelectedEnetIp));
                enetIpMenu.SetEnabled(interfaceAvailable && !commActive);
                enetIpMenu.SetVisible(_activityCommon.SelectedInterface == ActivityCommon.InterfaceType.Enet);
            }

            IMenuItem addErrorsMenu = menu.FindItem(Resource.Id.menu_xml_tool_add_errors_page);
            if (addErrorsMenu != null)
            {
                addErrorsMenu.SetEnabled(_ecuList.Count > 0);
                addErrorsMenu.SetChecked(_addErrorsPage);
            }

            IMenuItem cfgTypeSubMenu = menu.FindItem(Resource.Id.menu_xml_tool_submenu_cfg_type);
            if (cfgTypeSubMenu != null)
            {
                cfgTypeSubMenu.SetTitle(string.Format(Culture, "{0}: {1}", GetString(Resource.String.menu_xml_tool_cfg_type),
                    (_manualConfigIdx > 0) ?
                    GetString(Resource.String.xml_tool_man_config) + " " + _manualConfigIdx.ToString(CultureInfo.InvariantCulture) :
                    GetString(Resource.String.xml_tool_auto_config)));
                cfgTypeSubMenu.SetEnabled(interfaceAvailable && !commActive);
            }

            IMenuItem logSubMenu = menu.FindItem(Resource.Id.menu_submenu_log);
            logSubMenu?.SetEnabled(interfaceAvailable && !commActive);

            IMenuItem translationSubmenu = menu.FindItem(Resource.Id.menu_translation_submenu);
            if (translationSubmenu != null)
            {
                translationSubmenu.SetEnabled(true);
                translationSubmenu.SetVisible(ActivityCommon.IsTranslationRequired());
            }

            IMenuItem translationEnableMenu = menu.FindItem(Resource.Id.menu_translation_enable);
            if (translationEnableMenu != null)
            {
                translationEnableMenu.SetEnabled(!commActive || !string.IsNullOrWhiteSpace(ActivityCommon.YandexApiKey));
                translationEnableMenu.SetVisible(ActivityCommon.IsTranslationRequired());
                translationEnableMenu.SetChecked(ActivityCommon.EnableTranslation);
            }

            IMenuItem translationYandexKeyMenu = menu.FindItem(Resource.Id.menu_translation_yandex_key);
            if (translationYandexKeyMenu != null)
            {
                translationYandexKeyMenu.SetEnabled(!commActive);
                translationYandexKeyMenu.SetVisible(ActivityCommon.IsTranslationRequired());
            }

            IMenuItem translationClearCacheMenu = menu.FindItem(Resource.Id.menu_translation_clear_cache);
            if (translationClearCacheMenu != null)
            {
                translationClearCacheMenu.SetEnabled(!_activityCommon.IsTranslationCacheEmpty());
                translationClearCacheMenu.SetVisible(ActivityCommon.IsTranslationRequired());
            }

            return base.OnPrepareOptionsMenu(menu);
        }

        public override bool OnOptionsItemSelected(IMenuItem item)
        {
            switch (item.ItemId)
            {
                case Android.Resource.Id.Home:
                    if (IsJobRunning())
                    {
                        return true;
                    }
                    UpdateDisplay();
                    if (_buttonSafe.Enabled)
                    {
                        new AlertDialog.Builder(this)
                            .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                            {
                                SaveConfiguration(true);
                            })
                            .SetNegativeButton(Resource.String.button_no, (sender, args) =>
                            {
                                FinishContinue();
                            })
                            .SetMessage(Resource.String.xml_tool_msg_save_config)
                            .SetTitle(Resource.String.alert_title_question)
                            .Show();
                    }
                    else
                    {
                        FinishContinue();
                    }
                    return true;

                case Resource.Id.menu_tool_sel_interface:
                    if (IsJobRunning())
                    {
                        return true;
                    }
                    SelectInterface();
                    return true;

                case Resource.Id.menu_scan:
                    if (IsJobRunning())
                    {
                        return true;
                    }
                    _autoStart = false;
                    _activityCommon.SelectBluetoothDevice((int)ActivityRequest.RequestSelectDevice, _appDataDir);
                    return true;

                case Resource.Id.menu_adapter_config:
                    if (IsJobRunning())
                    {
                        return true;
                    }
                    AdapterConfig();
                    return true;

                case Resource.Id.menu_enet_ip:
                    if (IsJobRunning())
                    {
                        return true;
                    }
                    EnetIpConfig();
                    return true;

                case Resource.Id.menu_xml_tool_add_errors_page:
                    _addErrorsPage = !_addErrorsPage;
                    SupportInvalidateOptionsMenu();
                    UpdateDisplay();
                    return true;

                case Resource.Id.menu_xml_tool_submenu_cfg_type:
                    if (IsJobRunning())
                    {
                        return true;
                    }
                    SelectConfigTypeRequest();
                    return true;

                case Resource.Id.menu_submenu_log:
                    if (IsJobRunning())
                    {
                        return true;
                    }
                    SelectDataLogging();
                    return true;

                case Resource.Id.menu_translation_enable:
                    if (!ActivityCommon.EnableTranslation && string.IsNullOrWhiteSpace(ActivityCommon.YandexApiKey))
                    {
                        EditYandexKey();
                        return true;
                    }
                    ActivityCommon.EnableTranslation = !ActivityCommon.EnableTranslation;
                    SupportInvalidateOptionsMenu();
                    UpdateDisplay();
                    return true;

                case Resource.Id.menu_translation_yandex_key:
                    EditYandexKey();
                    return true;

                case Resource.Id.menu_translation_clear_cache:
                    _activityCommon.ClearTranslationCache();
                    ResetTranslations();
                    _ecuListTranslated = false;
                    SupportInvalidateOptionsMenu();
                    UpdateDisplay();
                    return true;

                case Resource.Id.menu_submenu_help:
                    _activityCommon.ShowWifiConnectedWarning(() =>
                    {
                        StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(@"http://ediabaslib.codeplex.com/wikipage?title=Configuration%20Generator")));
                    });
                    return true;
            }
            return base.OnOptionsItemSelected(item);
        }

        private void FinishContinue()
        {
            if (!SendTraceFile((sender, args) =>
            {
                Finish();
            }))
            {
                Finish();
            }
        }

        private void HandleStartDialogs()
        {
            if (_activityCommon.SelectedInterface == ActivityCommon.InterfaceType.None)
            {
                SelectInterface();
            }
            SelectInterfaceEnable();
            SupportInvalidateOptionsMenu();
            UpdateDisplay();
        }

        private void EdiabasOpen()
        {
            if (_ediabas == null)
            {
                _ediabas = new EdiabasNet
                {
                    EdInterfaceClass = _activityCommon.GetEdiabasInterfaceClass(),
                    AbortJobFunc = AbortEdiabasJob
                };
                _ediabas.SetConfigProperty("EcuPath", _ecuDir);
                UpdateLogInfo();
            }

            _ediabas.EdInterfaceClass.EnableTransmitCache = false;
            _activityCommon.SetEdiabasInterface(_ediabas, _deviceAddress);
        }

        // ReSharper disable once UnusedMethodReturnValue.Local
        private bool EdiabasClose()
        {
            if (IsJobRunning())
            {
                return false;
            }
            if (_ediabas != null)
            {
                _ediabas.Dispose();
                _ediabas = null;
            }
            UpdateDisplay();
            SupportInvalidateOptionsMenu();
            return true;
        }

        private bool IsJobRunning()
        {
            if (_jobThread == null)
            {
                return false;
            }
            if (_jobThread.IsAlive)
            {
                return true;
            }
            _jobThread = null;
            return false;
        }

        private bool SendTraceFile(EventHandler<EventArgs> handler)
        {
            if (_commErrorsOccured && _traceActive && !string.IsNullOrEmpty(_traceDir))
            {
                EdiabasClose();
                return _activityCommon.RequestSendTraceFile(_appDataDir, _traceDir, PackageManager.GetPackageInfo(PackageName, 0), GetType(), handler);
            }
            return false;
        }

        private void UpdateDisplay()
        {
            if (ActivityCommon.IsTranslationRequired() && ActivityCommon.EnableTranslation && string.IsNullOrWhiteSpace(ActivityCommon.YandexApiKey))
            {
                EditYandexKey();
                return;
            }
            _ecuListAdapter.Items.Clear();
            if (_ecuList.Count == 0)
            {
                _vin = string.Empty;
                _ecuList.Clear();
                _ecuListTranslated = false;
            }
            else
            {
                if (TranslateEcuText((sender, args) =>
                {
                    UpdateDisplay();
                }))
                {
                    return;
                }
                foreach (EcuInfo ecu in _ecuList)
                {
                    _ecuListAdapter.Items.Add(ecu);
                }
            }
            if (!ActivityCommon.EnableTranslation)
            {
                _ecuListTranslated = false;
            }

            _buttonRead.Text = GetString((_manualConfigIdx > 0) ?
                Resource.String.button_xml_tool_edit : Resource.String.button_xml_tool_read);
            _buttonRead.Enabled = _activityCommon.IsInterfaceAvailable();
            int selectedCount = _ecuList.Count(ecuInfo => ecuInfo.Selected);
            _buttonSafe.Enabled = (_ecuList.Count > 0) && (_addErrorsPage || (selectedCount > 0));
            _ecuListAdapter.NotifyDataSetChanged();

            string statusText = string.Empty;
            if (_ecuList.Count > 0)
            {
                statusText = GetString(Resource.String.xml_tool_ecu_list);
                if (!string.IsNullOrEmpty(_vin))
                {
                    statusText += " (" + GetString(Resource.String.xml_tool_info_vin) + ": " + _vin + ")";
                }
            }
            _textViewCarInfo.Text = statusText;
        }

        private bool TranslateEcuText(EventHandler<EventArgs> handler = null)
        {
            if (ActivityCommon.IsTranslationRequired() && ActivityCommon.EnableTranslation)
            {
                if (!_ecuListTranslated)
                {
                    _ecuListTranslated = true;
                    List<string> stringList = new List<string>();
                    foreach (EcuInfo ecu in _ecuList)
                    {
                        if (!string.IsNullOrEmpty(ecu.Description) && ecu.DescriptionTrans == null)
                        {
                            stringList.Add(ecu.Description);
                        }
                        if (ecu.JobList != null)
                        {
                            // ReSharper disable LoopCanBeConvertedToQuery
                            foreach (XmlToolEcuActivity.JobInfo jobInfo in ecu.JobList)
                            {
                                if (jobInfo.Comments != null && jobInfo.CommentsTrans == null &&
                                    XmlToolEcuActivity.IsValidJob(jobInfo))
                                {
                                    foreach (string comment in jobInfo.Comments)
                                    {
                                        if (!string.IsNullOrEmpty(comment))
                                        {
                                            stringList.Add(comment);
                                        }
                                    }
                                }
                                if (jobInfo.Results != null)
                                {
                                    foreach (XmlToolEcuActivity.ResultInfo result in jobInfo.Results)
                                    {
                                        if (result.Comments != null && result.CommentsTrans == null)
                                        {
                                            foreach (string comment in result.Comments)
                                            {
                                                if (!string.IsNullOrEmpty(comment))
                                                {
                                                    stringList.Add(comment);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            // ReSharper restore LoopCanBeConvertedToQuery
                        }
                    }
                    if (stringList.Count == 0)
                    {
                        return false;
                    }
                    if (_activityCommon.TranslateStrings(stringList, transList =>
                    {
                        try
                        {
                            if (transList != null && transList.Count == stringList.Count)
                            {
                                int transIndex = 0;
                                foreach (EcuInfo ecu in _ecuList)
                                {
                                    if (!string.IsNullOrEmpty(ecu.Description) && ecu.DescriptionTrans == null)
                                    {
                                        if (transIndex < transList.Count)
                                        {
                                            ecu.DescriptionTrans = transList[transIndex++];
                                        }
                                    }
                                    if (ecu.JobList != null)
                                    {
                                        foreach (XmlToolEcuActivity.JobInfo jobInfo in ecu.JobList)
                                        {
                                            if (jobInfo.Comments != null && jobInfo.CommentsTrans == null &&
                                                XmlToolEcuActivity.IsValidJob(jobInfo))
                                            {
                                                jobInfo.CommentsTrans = new List<string>();
                                                foreach (string comment in jobInfo.Comments)
                                                {
                                                    if (!string.IsNullOrEmpty(comment))
                                                    {
                                                        if (transIndex < transList.Count)
                                                        {
                                                            jobInfo.CommentsTrans.Add(transList[transIndex++]);
                                                        }
                                                    }
                                                }
                                            }
                                            if (jobInfo.Results != null)
                                            {
                                                foreach (XmlToolEcuActivity.ResultInfo result in jobInfo.Results)
                                                {
                                                    if (result.Comments != null && result.CommentsTrans == null)
                                                    {
                                                        result.CommentsTrans = new List<string>();
                                                        foreach (string comment in result.Comments)
                                                        {
                                                            if (!string.IsNullOrEmpty(comment))
                                                            {
                                                                if (transIndex < transList.Count)
                                                                {
                                                                    result.CommentsTrans.Add(transList[transIndex++]);
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                        handler?.Invoke(this, new EventArgs());
                    }))
                    {
                        return true;
                    }
                }
            }
            else
            {
                ResetTranslations();
            }
            return false;
        }

        private void ResetTranslations()
        {
            foreach (EcuInfo ecu in _ecuList)
            {
                ecu.DescriptionTrans = null;
                if (ecu.JobList != null)
                {
                    foreach (XmlToolEcuActivity.JobInfo jobInfo in ecu.JobList)
                    {
                        jobInfo.CommentsTrans = null;
                        if (jobInfo.Results != null)
                        {
                            foreach (XmlToolEcuActivity.ResultInfo result in jobInfo.Results)
                            {
                                result.CommentsTrans = null;
                            }
                        }
                    }
                }
            }
        }

        private void UpdateLogInfo()
        {
            if (_ediabas == null)
            {
                return;
            }
            string logDir = Path.Combine(_appDataDir, "LogConfigTool");
            try
            {
                Directory.CreateDirectory(logDir);
            }
            catch (Exception)
            {
                logDir = string.Empty;
            }

            _traceDir = null;
            if (_traceActive)
            {
                _traceDir = logDir;
            }

            if (!string.IsNullOrEmpty(_traceDir))
            {
                _ediabas.SetConfigProperty("TracePath", _traceDir);
                _ediabas.SetConfigProperty("IfhTrace", string.Format("{0}", (int)EdiabasNet.EdLogLevel.Error));
                _ediabas.SetConfigProperty("AppendTrace", _traceAppend ? "1" : "0");
                _ediabas.SetConfigProperty("CompressTrace", "1");
            }
            else
            {
                _ediabas.SetConfigProperty("IfhTrace", "0");
            }
        }

        private void SelectSgbdFile(bool groupFile)
        {
            // Launch the FilePickerActivity to select a sgbd file
            Intent serverIntent = new Intent(this, typeof(FilePickerActivity));
            serverIntent.PutExtra(FilePickerActivity.ExtraTitle, GetString(Resource.String.tool_select_sgbd));
            serverIntent.PutExtra(FilePickerActivity.ExtraInitDir, _ecuDir);
            serverIntent.PutExtra(FilePickerActivity.ExtraFileExtensions, groupFile ? ".grp" : ".prg");
            serverIntent.PutExtra(FilePickerActivity.ExtraDirChange, false);
            serverIntent.PutExtra(FilePickerActivity.ExtraShowExtension, false);
            StartActivityForResult(serverIntent, (int)ActivityRequest.RequestSelectSgbd);
        }

        private void SelectJobs(EcuInfo ecuInfo)
        {
            if (ecuInfo.JobList == null)
            {
                return;
            }
            XmlToolEcuActivity.IntentEcuInfo = ecuInfo;
            XmlToolEcuActivity.IntentEdiabas = _ediabas;
            Intent serverIntent = new Intent(this, typeof(XmlToolEcuActivity));
            serverIntent.PutExtra(XmlToolEcuActivity.ExtraEcuName, ecuInfo.Name);
            StartActivityForResult(serverIntent, (int)ActivityRequest.RequestSelectJobs);
        }

        private void EditYandexKey()
        {
            Intent serverIntent = new Intent(this, typeof(YandexKeyActivity));
            StartActivityForResult(serverIntent, (int)ActivityRequest.RequestYandexKey);
        }

        private void SelectInterface()
        {
            if (IsJobRunning())
            {
                return;
            }
            _activityCommon.SelectInterface((sender, args) =>
            {
                EdiabasClose();
                SupportInvalidateOptionsMenu();
                SelectInterfaceEnable();
            });
        }

        private void SelectInterfaceEnable()
        {
            _activityCommon.RequestInterfaceEnable((sender, args) =>
            {
                SupportInvalidateOptionsMenu();
            });
        }

        private void SelectDataLogging()
        {
            AlertDialog.Builder builder = new AlertDialog.Builder(this);
            builder.SetTitle(Resource.String.menu_submenu_log);
            ListView listView = new ListView(this);

            List<string> logNames = new List<string>
            {
                GetString(Resource.String.datalog_enable_trace),
                GetString(Resource.String.datalog_append_trace),
            };
            ArrayAdapter<string> adapter = new ArrayAdapter<string>(this,
                Android.Resource.Layout.SimpleListItemMultipleChoice, logNames.ToArray());
            listView.Adapter = adapter;
            listView.ChoiceMode = ChoiceMode.Multiple;
            listView.SetItemChecked(0, _traceActive);
            listView.SetItemChecked(1, _traceAppend);

            builder.SetView(listView);
            builder.SetPositiveButton(Resource.String.button_ok, (sender, args) =>
            {
                SparseBooleanArray sparseArray = listView.CheckedItemPositions;
                for (int i = 0; i < sparseArray.Size(); i++)
                {
                    bool value = sparseArray.ValueAt(i);
                    switch (sparseArray.KeyAt(i))
                    {
                        case 0:
                            _traceActive = value;
                            break;

                        case 1:
                            _traceAppend = value;
                            break;
                    }
                }
                UpdateLogInfo();
                SupportInvalidateOptionsMenu();
            });
            builder.SetNegativeButton(Resource.String.button_abort, (sender, args) =>
            {
            });
            builder.Show();
        }

        private void SelectConfigTypeRequest()
        {
            UpdateDisplay();
            if (_buttonSafe.Enabled)
            {
                new AlertDialog.Builder(this)
                    .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                    {
                        SaveConfiguration(false);
                        SelectConfigType();
                    })
                    .SetNegativeButton(Resource.String.button_no, (sender, args) =>
                    {
                        SelectConfigType();
                    })
                    .SetMessage(Resource.String.xml_tool_msg_save_config)
                    .SetTitle(Resource.String.alert_title_question)
                    .Show();
            }
            else
            {
                SelectConfigType();
            }
        }

        private void SelectConfigType()
        {
            AlertDialog.Builder builder = new AlertDialog.Builder(this);
            builder.SetTitle(Resource.String.menu_xml_tool_cfg_type);
            ListView listView = new ListView(this);

            List<string> manualNames = new List<string>();
            if (ActivityCommon.SelectedManufacturer == ActivityCommon.ManufacturerType.Bmw)
            {
                manualNames.Add(GetString(Resource.String.xml_tool_auto_config));
            }
            for (int i = 0; i < 4; i++)
            {
                manualNames.Add(GetString(Resource.String.xml_tool_man_config) + " " + (i + 1).ToString(CultureInfo.InvariantCulture));
            }
            ArrayAdapter<string> adapter = new ArrayAdapter<string>(this,
                Android.Resource.Layout.SimpleListItemSingleChoice, manualNames.ToArray());
            listView.Adapter = adapter;
            listView.ChoiceMode = ChoiceMode.Single;
            if (ActivityCommon.SelectedManufacturer == ActivityCommon.ManufacturerType.Bmw)
            {
                listView.SetItemChecked(_manualConfigIdx, true);
            }
            else
            {
                listView.SetItemChecked(_manualConfigIdx - 1, true);
            }

            builder.SetView(listView);
            builder.SetPositiveButton(Resource.String.button_ok, (sender, args) =>
            {
                if (ActivityCommon.SelectedManufacturer == ActivityCommon.ManufacturerType.Bmw)
                {
                    _manualConfigIdx = listView.CheckedItemPosition >= 0 ? listView.CheckedItemPosition : 0;
                }
                else
                {
                    _manualConfigIdx = listView.CheckedItemPosition + 1;
                }
                _vin = string.Empty;
                _ecuList.Clear();
                _ecuListTranslated = false;
                if (_manualConfigIdx > 0)
                {
                    EdiabasOpen();
                    ReadAllXml();
                    ExecuteUpdateEcuInfo();
                }
                SupportInvalidateOptionsMenu();
                UpdateDisplay();
            });
            builder.SetNegativeButton(Resource.String.button_abort, (sender, args) =>
            {
            });
            builder.Show();
        }

        private void ShowEditMenu(View anchor)
        {
            Android.Support.V7.Widget.PopupMenu popupEdit = new Android.Support.V7.Widget.PopupMenu(this, anchor);
            popupEdit.Inflate(Resource.Menu.xml_tool_edit);
            IMenuItem detectMenuMenu = popupEdit.Menu.FindItem(Resource.Id.menu_xml_tool_edit_detect);
            detectMenuMenu?.SetVisible(ActivityCommon.SelectedManufacturer != ActivityCommon.ManufacturerType.Bmw);
            popupEdit.MenuItemClick += (sender, args) =>
            {
                switch (args.Item.ItemId)
                {
                    case Resource.Id.menu_xml_tool_edit_detect:
                        if (_ecuList.Count > 0)
                        {
                            new AlertDialog.Builder(this)
                                .SetPositiveButton(Resource.String.button_yes, (s, a) =>
                                {
                                    _ecuList.Clear();
                                    _ecuListTranslated = false;
                                    UpdateDisplay();
                                    ExecuteAnalyzeJob();
                                })
                                .SetNegativeButton(Resource.String.button_no, (s, a) =>
                                {
                                    ExecuteAnalyzeJob();
                                })
                                .SetCancelable(true)
                                .SetMessage(Resource.String.xml_tool_clear_ecus)
                                .SetTitle(Resource.String.alert_title_question)
                                .Show();
                            break;
                        }
                        ExecuteAnalyzeJob();
                        break;

                    case Resource.Id.menu_xml_tool_edit_grp:
                    case Resource.Id.menu_xml_tool_edit_prg:
                        SelectSgbdFile(args.Item.ItemId == Resource.Id.menu_xml_tool_edit_grp);
                        break;

                    case Resource.Id.menu_xml_tool_edit_del:
                    {
                        for (int i = 0; i < _ecuList.Count; i++)
                        {
                            EcuInfo ecuInfo = _ecuList[i];
                            if (!ecuInfo.Selected)
                            {
                                _ecuList.Remove(ecuInfo);
                                i = 0;
                            }
                        }
                        UpdateDisplay();
                        break;
                    }

                    case Resource.Id.menu_xml_tool_edit_del_all:
                        new AlertDialog.Builder(this)
                            .SetPositiveButton(Resource.String.button_yes, (s, a) =>
                            {
                                DeleteAllXml();
                            })
                            .SetNegativeButton(Resource.String.button_no, (s, a) =>
                            {
                            })
                            .SetCancelable(true)
                            .SetMessage(Resource.String.xml_tool_del_all_info)
                            .SetTitle(Resource.String.alert_title_question)
                            .Show();
                        break;
                }
            };
            popupEdit.Show();
        }

        private void ShowContextMenu(View anchor, int itemPos)
        {
            Android.Support.V7.Widget.PopupMenu popupContext = new Android.Support.V7.Widget.PopupMenu(this, anchor);
            popupContext.Inflate(Resource.Menu.xml_tool_context);
            IMenuItem moveTopMenu = popupContext.Menu.FindItem(Resource.Id.menu_xml_tool_move_top);
            moveTopMenu?.SetEnabled(itemPos > 0);

            IMenuItem moveUpMenu = popupContext.Menu.FindItem(Resource.Id.menu_xml_tool_move_up);
            moveUpMenu?.SetEnabled(itemPos > 0);

            IMenuItem moveDownMenu = popupContext.Menu.FindItem(Resource.Id.menu_xml_tool_move_down);
            moveDownMenu?.SetEnabled((itemPos + 1) < _ecuListAdapter.Items.Count);

            IMenuItem moveBottomMenu = popupContext.Menu.FindItem(Resource.Id.menu_xml_tool_move_bottom);
            moveBottomMenu?.SetEnabled((itemPos + 1) < _ecuListAdapter.Items.Count);

            popupContext.MenuItemClick += (sender, args) =>
            {
                switch (args.Item.ItemId)
                {
                    case Resource.Id.menu_xml_tool_move_top:
                    {
                        EcuInfo oldItem = _ecuList[itemPos];
                        _ecuList.RemoveAt(itemPos);
                        _ecuList.Insert(0, oldItem);
                        UpdateDisplay();
                        break;
                    }

                    case Resource.Id.menu_xml_tool_move_up:
                    {
                        EcuInfo oldItem = _ecuList[itemPos - 1];
                        _ecuList[itemPos - 1] = _ecuList[itemPos];
                        _ecuList[itemPos] = oldItem;
                        UpdateDisplay();
                        break;
                    }

                    case Resource.Id.menu_xml_tool_move_down:
                    {
                        EcuInfo oldItem = _ecuList[itemPos + 1];
                        _ecuList[itemPos + 1] = _ecuList[itemPos];
                        _ecuList[itemPos] = oldItem;
                        UpdateDisplay();
                        break;
                    }

                    case Resource.Id.menu_xml_tool_move_bottom:
                    {
                        EcuInfo oldItem = _ecuList[itemPos];
                        _ecuList.RemoveAt(itemPos);
                        _ecuList.Add(oldItem);
                        UpdateDisplay();
                        break;
                    }
                }
            };
            popupContext.Show();
        }

        private void AdapterConfig()
        {
            EdiabasClose();
            if (_activityCommon.SelectedInterface == ActivityCommon.InterfaceType.Enet)
            {
                _activityCommon.EnetAdapterConfig();
                return;
            }
            Intent serverIntent = new Intent(this, typeof(CanAdapterActivity));
            serverIntent.PutExtra(CanAdapterActivity.ExtraDeviceAddress, _deviceAddress);
            serverIntent.PutExtra(CanAdapterActivity.ExtraInterfaceType, (int)_activityCommon.SelectedInterface);
            StartActivityForResult(serverIntent, (int)ActivityRequest.RequestCanAdapterConfig);
        }

        private void EnetIpConfig()
        {
            EdiabasClose();
            _activityCommon.SelectEnetIp((sender, args) =>
            {
                SupportInvalidateOptionsMenu();
            });
        }

        private void PerformAnalyze()
        {
            if (IsJobRunning())
            {
                return;
            }
            _autoStart = false;
            if (string.IsNullOrEmpty(_deviceAddress))
            {
                if (!_activityCommon.RequestBluetoothDeviceSelect((int)ActivityRequest.RequestSelectDevice, _appDataDir, (sender, args) =>
                {
                    _autoStart = true;
                }))
                {
                    return;
                }
            }
            if (_activityCommon.ShowWifiWarning(noAction =>
            {
                if (noAction)
                {
                    PerformAnalyze();
                }
            }))
            {
                return;
            }
            ExecuteAnalyzeJob();
        }

        private void ExecuteAnalyzeJob()
        {
            if (ActivityCommon.SelectedManufacturer == ActivityCommon.ManufacturerType.Bmw)
            {
                ExecuteAnalyzeJobBmw();
                return;
            }
            ExecuteAnalyzeJobVag();
        }

        private void ExecuteAnalyzeJobBmw()
        {
            EdiabasOpen();
            _vin = string.Empty;
            _ecuList.Clear();
            _ecuListTranslated = false;
            UpdateDisplay();

            Android.App.ProgressDialog progress = new Android.App.ProgressDialog(this);
            progress.SetCancelable(false);
            progress.SetMessage(GetString(Resource.String.xml_tool_analyze));
            progress.Show();

            _ediabasJobAbort = false;
            _jobThread = new Thread(() =>
            {
                int bestInvalidCount = 0;
                int bestInvalidVinCount = 0;
                List<EcuInfo> ecuListBest = null;
                string ecuFileNameBest = null;
                _ediabas.EdInterfaceClass.EnableTransmitCache = true;
                foreach (string fileName in EcuFileNames)
                {
                    try
                    {
                        int invalidEcuCount = 0;

                        _ediabas.ResolveSgbdFile(fileName);

                        _ediabas.ArgString = string.Empty;
                        _ediabas.ArgBinaryStd = null;
                        _ediabas.ResultsRequests = string.Empty;
                        _ediabas.ExecuteJob("IDENT_FUNKTIONAL");

                        List<EcuInfo> ecuList = new List<EcuInfo>();
                        List<long> invalidAddrList = new List<long>();
                        List<Dictionary<string, EdiabasNet.ResultData>> resultSets = _ediabas.ResultSets;
                        if (resultSets != null && resultSets.Count >= 2)
                        {
                            int dictIndex = 0;
                            foreach (Dictionary<string, EdiabasNet.ResultData> resultDict in resultSets)
                            {
                                if (dictIndex == 0)
                                {
                                    dictIndex++;
                                    continue;
                                }
                                bool ecuDataPresent = false;
                                string ecuName = string.Empty;
                                Int64 ecuAdr = -1;
                                string ecuDesc = string.Empty;
                                string ecuSgbd = string.Empty;
                                string ecuGroup = string.Empty;
                                Int64 dateYear = 0;
                                EdiabasNet.ResultData resultData;
                                if (resultDict.TryGetValue("ECU_GROBNAME", out resultData))
                                {
                                    if (resultData.OpData is string)
                                    {
                                        ecuName = (string) resultData.OpData;
                                    }
                                }
                                if (resultDict.TryGetValue("ID_SG_ADR", out resultData))
                                {
                                    if (resultData.OpData is Int64)
                                    {
                                        ecuAdr = (Int64) resultData.OpData;
                                    }
                                }
                                if (resultDict.TryGetValue("ECU_NAME", out resultData))
                                {
                                    if (resultData.OpData is string)
                                    {
                                        ecuDesc = (string) resultData.OpData;
                                    }
                                }
                                if (resultDict.TryGetValue("ECU_SGBD", out resultData))
                                {
                                    ecuDataPresent = true;
                                    if (resultData.OpData is string)
                                    {
                                        ecuSgbd = (string) resultData.OpData;
                                    }
                                }
                                if (resultDict.TryGetValue("ECU_GRUPPE", out resultData))
                                {
                                    ecuDataPresent = true;
                                    if (resultData.OpData is string)
                                    {
                                        ecuGroup = (string) resultData.OpData;
                                    }
                                }
                                if (resultDict.TryGetValue("ID_DATUM_JAHR", out resultData))
                                {
                                    if (resultData.OpData is Int64)
                                    {
                                        dateYear = (Int64) resultData.OpData;
                                    }
                                }
                                if (!string.IsNullOrEmpty(ecuName) && ecuAdr >= 0 && !string.IsNullOrEmpty(ecuSgbd))
                                {
                                    if (ecuList.All(ecuInfo => ecuInfo.Address != ecuAdr))
                                    {
                                        // address not existing
                                        ecuList.Add(new EcuInfo(ecuName, ecuAdr, ecuDesc, ecuSgbd, ecuGroup));
                                    }
                                }
                                else
                                {
                                    if (ecuDataPresent)
                                    {
                                        if (!ecuName.StartsWith("VIRTSG", StringComparison.OrdinalIgnoreCase) && (dateYear != 0))
                                        {
                                            invalidAddrList.Add(ecuAdr);
                                        }
                                    }
                                }
                                dictIndex++;
                            }
                            // ReSharper disable once LoopCanBeConvertedToQuery
                            foreach (long addr in invalidAddrList)
                            {
                                if (ecuList.All(ecuInfo => ecuInfo.Address != addr))
                                {
                                    invalidEcuCount++;
                                }
                            }
                        }

                        _ediabas.ArgString = string.Empty;
                        _ediabas.ArgBinaryStd = null;
                        _ediabas.ResultsRequests = string.Empty;
                        bool readVinOk = false;
                        foreach (string vinJob in ReadVinJobs)
                        {
                            try
                            {
                                _ediabas.ExecuteJob(vinJob);
                                readVinOk = true;
                                break;
                            }
                            catch (Exception)
                            {
                                // ignored
                            }
                        }

                        _ediabas.LogFormat(EdiabasNet.EdLogLevel.Ifh, "Detect result: count={0}, invalid={1}, vinok={2}", ecuList.Count, invalidEcuCount, readVinOk);
                        int invalidVinCount = readVinOk ? 0 : 1;
                        bool acceptEcu = false;
                        if (ecuListBest == null)
                        {
                            acceptEcu = true;
                        }
                        else
                        {
                            if (ecuListBest.Count < ecuList.Count)
                            {
                                acceptEcu = true;
                            }
                            else
                            {
                                if (ecuListBest.Count == ecuList.Count && (bestInvalidCount + bestInvalidVinCount) > (invalidEcuCount + invalidVinCount))
                                {
                                    acceptEcu = true;
                                }
                            }
                        }
                        if (acceptEcu)
                        {
                            _ediabas.LogString(EdiabasNet.EdLogLevel.Ifh, "Selected ECU");
                            ecuListBest = ecuList;
                            ecuFileNameBest = fileName;
                            bestInvalidCount = invalidEcuCount;
                            bestInvalidVinCount = invalidVinCount;
                        }
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }
                if (ecuListBest != null)
                {
                    _ediabas.LogFormat(EdiabasNet.EdLogLevel.Ifh, "Selected Ecu file: {0}", ecuFileNameBest);
                    _ecuList.AddRange(ecuListBest.OrderBy(x => x.Name));

                    try
                    {
                        _ediabas.ResolveSgbdFile(ecuFileNameBest);
                        _ediabas.ArgString = string.Empty;
                        _ediabas.ArgBinaryStd = null;
                        _ediabas.ResultsRequests = string.Empty;
                        bool readVinOk = false;
                        foreach (string vinJob in ReadVinJobs)
                        {
                            try
                            {
                                _ediabas.ExecuteJob(vinJob);
                                readVinOk = true;
                                break;
                            }
                            catch (Exception)
                            {
                                // ignored
                            }
                        }
                        if (!readVinOk)
                        {
                            throw new Exception("Read VIN failed");
                        }

                        Regex regex = new Regex(@"^[a-zA-Z][a-zA-Z0-9]+$");
                        List<Dictionary<string, EdiabasNet.ResultData>> resultSets = _ediabas.ResultSets;
                        if (resultSets != null && resultSets.Count >= 2)
                        {
                            int dictIndex = 0;
                            foreach (Dictionary<string, EdiabasNet.ResultData> resultDict in resultSets)
                            {
                                if (dictIndex == 0)
                                {
                                    dictIndex++;
                                    continue;
                                }
                                Int64 ecuAdr = -1;
                                string ecuVin = string.Empty;
                                EdiabasNet.ResultData resultData;
                                if (resultDict.TryGetValue("ID_SG_ADR", out resultData))
                                {
                                    if (resultData.OpData is Int64)
                                    {
                                        ecuAdr = (Int64) resultData.OpData;
                                    }
                                }
                                if (resultDict.TryGetValue("FG_NR", out resultData))
                                {
                                    if (resultData.OpData is string)
                                    {
                                        ecuVin = (string) resultData.OpData;
                                    }
                                }
                                if (resultDict.TryGetValue("FG_NR_KURZ", out resultData))
                                {
                                    if (resultData.OpData is string)
                                    {
                                        ecuVin = (string) resultData.OpData;
                                    }
                                }
                                if (resultDict.TryGetValue("AIF_FG_NR", out resultData))
                                {
                                    if (resultData.OpData is string)
                                    {
                                        ecuVin = (string) resultData.OpData;
                                    }
                                }
                                if (!string.IsNullOrEmpty(ecuVin) && regex.IsMatch(ecuVin))
                                {
                                    foreach (EcuInfo ecuInfo in _ecuList)
                                    {
                                        if (ecuInfo.Address == ecuAdr)
                                        {
                                            ecuInfo.Vin = ecuVin;
                                            break;
                                        }
                                    }
                                }
                                dictIndex++;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // ignored
                    }

                    // get vin
                    var vinInfo = _ecuList.GroupBy(x => x.Vin)
                        .Where(x => !string.IsNullOrEmpty(x.Key))
                        .OrderByDescending(x => x.Count())
                        .FirstOrDefault();
                    _vin = vinInfo != null ? vinInfo.Key : string.Empty;
                    ReadAllXml();
                }
                _ediabas.EdInterfaceClass.EnableTransmitCache = false;

                RunOnUiThread(() =>
                {
                    progress.Hide();
                    progress.Dispose();

                    SupportInvalidateOptionsMenu();
                    UpdateDisplay();

                    if (ecuListBest == null)
                    {
                        _commErrorsOccured = true;
                        AlertDialog altertDialog = new AlertDialog.Builder(this)
                            .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                            {
                                SelectConfigTypeRequest();
                            })
                            .SetNegativeButton(Resource.String.button_no, (sender, args) =>
                            {
                            })
                            .SetCancelable(true)
                            .SetMessage(Resource.String.xml_tool_no_response_manual)
                            .SetTitle(Resource.String.alert_title_warning)
                            .Show();
                        TextView messageView = altertDialog.FindViewById<TextView>(Android.Resource.Id.Message);
                        if (messageView != null)
                        {
                            messageView.MovementMethod = new LinkMovementMethod();
                        }
                    }
                    else
                    {
                        if (bestInvalidCount > 0)
                        {
                            _commErrorsOccured = true;
                            _activityCommon.ShowAlert(GetString(Resource.String.xml_tool_msg_ecu_error), Resource.String.alert_title_warning);
                        }
                    }
                });
            });
            _jobThread.Start();
        }

        private void ExecuteAnalyzeJobVag()
        {
            List<ActivityCommon.VagEcuEntry> ecuVagList = ActivityCommon.ReadVagEcuList(_ecuDir);
            if ((ecuVagList == null) || (ecuVagList.Count == 0))
            {
                _activityCommon.ShowAlert(GetString(Resource.String.xml_tool_read_ecu_info_failed), Resource.String.alert_title_error);
                return;
            }

            EdiabasOpen();
            _vin = string.Empty;

            Android.App.ProgressDialog progress = new Android.App.ProgressDialog(this);
            progress.SetCancelable(false);
            progress.SetMessage(GetString(Resource.String.xml_tool_analyze));
            progress.SetProgressStyle(Android.App.ProgressDialogStyle.Horizontal);
            progress.Progress = 0;
            progress.Max = 100;
            progress.SetButton((int) DialogButtonType.Negative, GetString(Resource.String.button_abort), (sender, args) =>
            {
                _ediabasJobAbort = true;
                progress = new Android.App.ProgressDialog(this);
                progress.SetCancelable(false);
                progress.SetMessage(GetString(Resource.String.xml_tool_aborting));
                progress.Show();
            });
            progress.Show();
            _activityCommon.SetScreenLock(true);

            _ediabasJobAbort = false;
            _jobThread = new Thread(() =>
            {
                Dictionary<int, string> ecuNameDict = ActivityCommon.GetVagEcuNamesDict(_ecuDir);
                int maxEcuAddress = 0xFF;
                int ecuCount = ecuVagList.Count;
                int index = 0;
                int detectCount = 0;
                foreach (ActivityCommon.VagEcuEntry ecuEntry in ecuVagList)
                {
                    if (_ediabasJobAbort)
                    {
                        break;
                    }
#if false
                    if (index > 3)
                    {
                        break;
                    }
#endif
                    if (ecuEntry.Address > maxEcuAddress)
                    {
                        index++;
                        continue;
                    }
                    int localIndex = index;
                    int localDetectCount = detectCount;
                    int localEcuCount = ecuCount;
                    RunOnUiThread(() =>
                    {
                        if (!_ediabasJobAbort)
                        {
                            progress.Progress = 100 * localIndex / localEcuCount;
                            progress.SetMessage(string.Format(GetString(Resource.String.xml_tool_search_ecus), localDetectCount, localIndex));
                        }
                    }
                    );
                    try
                    {
                        _ediabas.ResolveSgbdFile(ecuEntry.SysName);
                        _ediabas.ArgString = string.Empty;
                        _ediabas.ArgBinaryStd = null;
                        _ediabas.ResultsRequests = string.Empty;

                        _ediabas.ExecuteJob("Steuergeraeteversion_abfragen");
                        List<Dictionary<string, EdiabasNet.ResultData>> resultSets = _ediabas.ResultSets;
                        if (resultSets != null && resultSets.Count >= 2)
                        {
                            Dictionary<string, EdiabasNet.ResultData> resultDict = resultSets[0];
                            EdiabasNet.ResultData resultData;
                            if (resultDict.TryGetValue("JOBSTATUS", out resultData))
                            {
                                if (resultData.OpData is string)
                                {
                                    string result = (string)resultData.OpData;
                                    if (string.Compare(result, "OKAY", StringComparison.OrdinalIgnoreCase) == 0)
                                    {
                                        detectCount++;
                                        string ecuName = Path.GetFileNameWithoutExtension(_ediabas.SgbdFileName) ?? string.Empty;
                                        bool ecuFound = _ecuList.Any(ecuInfo => string.Compare(ecuInfo.Sgbd, ecuName, StringComparison.OrdinalIgnoreCase) == 0);
                                        if (!ecuFound)
                                        {
                                            string displayName;
                                            if ((ecuNameDict == null) || !ecuNameDict.TryGetValue(ecuEntry.Address, out displayName))
                                            {
                                                displayName = ecuName;
                                            }
                                            EcuInfo ecuInfo = new EcuInfo(ecuName.ToUpperInvariant(), ecuEntry.Address, string.Empty, ecuName, string.Empty)
                                            {
                                                PageName = displayName,
                                                EcuName = displayName
                                            };
                                            _ecuList.Add(ecuInfo);
                                        }
                                    }
                                }
                            }
                        }
                        if (ecuEntry.Address == 1)
                        {   // motor ECU, check communication interface
                            string sgbdFileName = _ediabas.SgbdFileName.ToUpperInvariant();
                            if (sgbdFileName.Contains("2000") || sgbdFileName.Contains("1281"))
                            {   // bit 7 is parity bit
                                maxEcuAddress = 0x7F;
                                ecuCount = ecuVagList.Count(x => x.Address <= maxEcuAddress);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        if (ecuEntry.Address == 1)
                        {   // motor must be present, abort
                            break;
                        }
                    }
                    index++;
                }

                RunOnUiThread(() =>
                {
                    progress.Hide();
                    progress.Dispose();
                    _activityCommon.SetScreenLock(false);

                    SupportInvalidateOptionsMenu();
                    UpdateDisplay();

                    if (!_ediabasJobAbort && ((_ecuList.Count == 0) || (detectCount == 0)))
                    {
                        _commErrorsOccured = true;
                        _activityCommon.ShowAlert(GetString(Resource.String.xml_tool_no_response), Resource.String.alert_title_error);
                    }
                });
            });
            _jobThread.Start();
        }

        private void ExecuteJobsRead(EcuInfo ecuInfo)
        {
            EdiabasOpen();
            if (ecuInfo.JobList != null)
            {
                SelectJobs(ecuInfo);
                return;
            }

            UpdateDisplay();

            Android.App.ProgressDialog progress = new Android.App.ProgressDialog(this);
            progress.SetCancelable(false);
            progress.SetMessage(GetString(Resource.String.xml_tool_analyze));
            if ((ActivityCommon.SelectedManufacturer != ActivityCommon.ManufacturerType.Bmw) &&
                (string.IsNullOrEmpty(ecuInfo.MwTabFileName) || !File.Exists(ecuInfo.MwTabFileName)))
            {
                progress.SetProgressStyle(Android.App.ProgressDialogStyle.Horizontal);
                progress.Progress = 0;
                progress.Max = 100;
            }
            progress.SetButton((int)DialogButtonType.Negative, GetString(Resource.String.button_abort), (sender, args) =>
            {
                _ediabasJobAbort = true;
                progress = new Android.App.ProgressDialog(this);
                progress.SetCancelable(false);
                progress.SetMessage(GetString(Resource.String.xml_tool_aborting));
                progress.Show();
            });
            progress.Show();
            _activityCommon.SetScreenLock(true);

            _ediabasJobAbort = false;
            _jobThread = new Thread(() =>
            {
                bool readFailed = false;
                try
                {
                    _ediabas.ResolveSgbdFile(ecuInfo.Sgbd);

                    _ediabas.ArgString = "ALL";
                    _ediabas.ArgBinaryStd = null;
                    _ediabas.ResultsRequests = string.Empty;
                    _ediabas.NoInitForVJobs = true;
                    _ediabas.ExecuteJob("_JOBS");

                    List<XmlToolEcuActivity.JobInfo> jobList = new List<XmlToolEcuActivity.JobInfo>();
                    List<Dictionary<string, EdiabasNet.ResultData>> resultSets = _ediabas.ResultSets;
                    if (resultSets != null && resultSets.Count >= 2)
                    {
                        int dictIndex = 0;
                        foreach (Dictionary<string, EdiabasNet.ResultData> resultDict in resultSets)
                        {
                            if (dictIndex == 0)
                            {
                                dictIndex++;
                                continue;
                            }
                            EdiabasNet.ResultData resultData;
                            if (resultDict.TryGetValue("JOBNAME", out resultData))
                            {
                                if (resultData.OpData is string)
                                {
                                    jobList.Add(new XmlToolEcuActivity.JobInfo((string)resultData.OpData));
                                }
                            }
                            dictIndex++;
                        }
                    }

                    foreach (XmlToolEcuActivity.JobInfo job in jobList)
                    {
                        _ediabas.ArgString = job.Name;
                        _ediabas.ArgBinaryStd = null;
                        _ediabas.ResultsRequests = string.Empty;
                        _ediabas.NoInitForVJobs = true;
                        _ediabas.ExecuteJob("_JOBCOMMENTS");

                        resultSets = _ediabas.ResultSets;
                        if (resultSets != null && resultSets.Count >= 2)
                        {
                            Dictionary<string, EdiabasNet.ResultData> resultDict = resultSets[1];
                            for (int i = 0; ; i++)
                            {
                                EdiabasNet.ResultData resultData;
                                if (resultDict.TryGetValue("JOBCOMMENT" + i.ToString(Culture), out resultData))
                                {
                                    if (resultData.OpData is string)
                                    {
                                        job.Comments.Add((string)resultData.OpData);
                                    }
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }
                    }

                    foreach (XmlToolEcuActivity.JobInfo job in jobList)
                    {
                        _ediabas.ArgString = job.Name;
                        _ediabas.ArgBinaryStd = null;
                        _ediabas.ResultsRequests = string.Empty;
                        _ediabas.ExecuteJob("_ARGUMENTS");

                        resultSets = _ediabas.ResultSets;
                        if (resultSets != null && resultSets.Count >= 2)
                        {
                            int dictIndex = 0;
                            foreach (Dictionary<string, EdiabasNet.ResultData> resultDict in resultSets)
                            {
                                if (dictIndex == 0)
                                {
                                    dictIndex++;
                                    continue;
                                }
                                EdiabasNet.ResultData resultData;
                                uint argCount = 0;
                                if (resultDict.TryGetValue("ARG", out resultData))
                                {
                                    if (resultData.OpData is string)
                                    {
                                        argCount++;
                                    }
                                }
                                job.ArgCount = argCount;
                                dictIndex++;
                            }
                        }
                    }

                    if (ActivityCommon.SelectedManufacturer != ActivityCommon.ManufacturerType.Bmw)
                    {
                        if (string.IsNullOrEmpty(ecuInfo.MwTabFileName) || !File.Exists(ecuInfo.MwTabFileName))
                        {
                            List<string> mwTabFileNames = GetBestMatchingMwTab(ecuInfo, progress);
                            if (mwTabFileNames == null)
                            {
                                throw new Exception("Read mwtab jobs failed");
                            }
                            if (mwTabFileNames.Count == 0)
                            {
                                ecuInfo.MwTabFileName = string.Empty;
                            }
                            else if (mwTabFileNames.Count == 1)
                            {
                                ecuInfo.MwTabFileName = mwTabFileNames[0];
                            }
                            else
                            {
                                RunOnUiThread(() =>
                                {
                                    SelectMwTabFromListInfo(mwTabFileNames, name =>
                                    {
                                        if (string.IsNullOrEmpty(name))
                                        {
                                            ReadJobThreadDone(ecuInfo, progress, true);
                                        }
                                        else
                                        {
                                            ecuInfo.MwTabFileName = name;
                                            ecuInfo.MwTabList = ActivityCommon.ReadVagMwTab(ecuInfo.MwTabFileName);
                                            ecuInfo.ReadCommand = GetReadCommand(ecuInfo);
                                            _jobThread = new Thread(() =>
                                            {
                                                readFailed = false;
                                                try
                                                {
                                                    JobsReadThreadPart2(ecuInfo, jobList);
                                                }
                                                catch (Exception)
                                                {
                                                    readFailed = true;
                                                }
                                                RunOnUiThread(() =>
                                                {
                                                    ReadJobThreadDone(ecuInfo, progress, readFailed);
                                                });
                                            });
                                            _jobThread.Start();
                                        }
                                    });
                                });
                                return;
                            }
                        }
                    }
                    ecuInfo.MwTabList = string.IsNullOrEmpty(ecuInfo.MwTabFileName) ? null : ActivityCommon.ReadVagMwTab(ecuInfo.MwTabFileName);
                    ecuInfo.ReadCommand = GetReadCommand(ecuInfo);

                    JobsReadThreadPart2(ecuInfo, jobList);
                }
                catch (Exception)
                {
                    readFailed = true;
                }

                RunOnUiThread(() =>
                {
                    ReadJobThreadDone(ecuInfo, progress, readFailed);
                });
            });
            _jobThread.Start();
        }

        private void JobsReadThreadPart2(EcuInfo ecuInfo, List<XmlToolEcuActivity.JobInfo> jobList)
        {
            foreach (XmlToolEcuActivity.JobInfo job in jobList)
            {
                if (ActivityCommon.SelectedManufacturer != ActivityCommon.ManufacturerType.Bmw && ecuInfo.MwTabList != null)
                {
                    if (string.Compare(job.Name, "Messwerteblock_lesen", StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        job.Comments = new List<string> {GetString(Resource.String.xml_tool_job_read_mwblock)};
                        foreach (ActivityCommon.MwTabEntry mwTabEntry in ecuInfo.MwTabList)
                        {
                            string name = string.Format(Culture, "{0}/{1}", mwTabEntry.BlockNumber, mwTabEntry.ValueIndex);
                            string displayText = string.Format(Culture, "{0:000}/{1} {2}", mwTabEntry.BlockNumber, mwTabEntry.ValueIndex, mwTabEntry.Description);
                            string comment = string.Empty;
                            if (mwTabEntry.ValueMin != null && mwTabEntry.ValueMax != null)
                            {
                                comment = string.Format(Culture, "{0} - {1}", mwTabEntry.ValueMin, mwTabEntry.ValueMax);
                            }
                            else if (mwTabEntry.ValueMin != null)
                            {
                                comment = string.Format(Culture, "> {0}", mwTabEntry.ValueMin);
                            }
                            else if (mwTabEntry.ValueMax != null)
                            {
                                comment = string.Format(Culture, "< {0}", mwTabEntry.ValueMax);
                            }
                            if (!string.IsNullOrEmpty(mwTabEntry.ValueUnit))
                            {
                                comment += string.Format(Culture, " [{0}]", mwTabEntry.ValueUnit);
                            }
                            List<string> commentList = new List<string>();
                            if (!string.IsNullOrEmpty(comment))
                            {
                                commentList.Add(comment);
                            }
                            if (!string.IsNullOrEmpty(mwTabEntry.Comment))
                            {
                                commentList.Add(mwTabEntry.Comment);
                            }

                            string type = (string.Compare(mwTabEntry.ValueType, "R", StringComparison.OrdinalIgnoreCase) == 0) ? "real" : "integer";
                            job.Results.Add(new XmlToolEcuActivity.ResultInfo(name, displayText, type, commentList, mwTabEntry));
                        }
                    }
                    else if (string.Compare(job.Name, "Fahrgestellnr_abfragen", StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        job.Comments = new List<string> { GetString(Resource.String.xml_tool_job_read_vin) };
                        job.Results.Add(new XmlToolEcuActivity.ResultInfo("Fahrgestellnr", GetString(Resource.String.xml_tool_result_vin), "string", null));
                    }
                    continue;
                }

                _ediabas.ArgString = job.Name;
                _ediabas.ArgBinaryStd = null;
                _ediabas.ResultsRequests = string.Empty;
                _ediabas.NoInitForVJobs = true;
                _ediabas.ExecuteJob("_RESULTS");

                List<Dictionary<string, EdiabasNet.ResultData>> resultSets = _ediabas.ResultSets;
                if (resultSets != null && resultSets.Count >= 2)
                {
                    int dictIndex = 0;
                    foreach (Dictionary<string, EdiabasNet.ResultData> resultDict in resultSets)
                    {
                        if (dictIndex == 0)
                        {
                            dictIndex++;
                            continue;
                        }
                        EdiabasNet.ResultData resultData;
                        string result = string.Empty;
                        string resultType = string.Empty;
                        List<string> resultCommentList = new List<string>();
                        if (resultDict.TryGetValue("RESULT", out resultData))
                        {
                            if (resultData.OpData is string)
                            {
                                result = (string) resultData.OpData;
                            }
                        }
                        if (resultDict.TryGetValue("RESULTTYPE", out resultData))
                        {
                            if (resultData.OpData is string)
                            {
                                resultType = (string) resultData.OpData;
                            }
                        }
                        for (int i = 0;; i++)
                        {
                            if (resultDict.TryGetValue("RESULTCOMMENT" + i.ToString(Culture), out resultData))
                            {
                                if (resultData.OpData is string)
                                {
                                    resultCommentList.Add((string) resultData.OpData);
                                }
                            }
                            else
                            {
                                break;
                            }
                        }
                        job.Results.Add(new XmlToolEcuActivity.ResultInfo(result, result, resultType, resultCommentList));
                        dictIndex++;
                    }
                }
            }

            ecuInfo.JobList = jobList;

            if (ecuInfo.IgnoreXmlFile)
            {
                ecuInfo.PageName = ecuInfo.EcuName;
            }
            else
            {
                string xmlFileDir = XmlFileDir();
                if (xmlFileDir != null)
                {
                    string xmlFile = Path.Combine(xmlFileDir, ActivityCommon.CreateValidFileName(ecuInfo.Name + PageExtension));
                    if (File.Exists(xmlFile))
                    {
                        try
                        {
                            ReadPageXml(ecuInfo, XDocument.Load(xmlFile));
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }
                }
            }
        }

        private void ReadJobThreadDone(EcuInfo ecuInfo, Android.App.ProgressDialog progress, bool readFailed)
        {
            progress.Hide();
            progress.Dispose();
            _activityCommon.SetScreenLock(false);

            SupportInvalidateOptionsMenu();
            UpdateDisplay();
            if (_ediabasJobAbort || ecuInfo.JobList == null)
            {
                return;
            }
            if (readFailed || (ecuInfo.JobList.Count == 0))
            {
                _activityCommon.ShowAlert(GetString(Resource.String.xml_tool_read_jobs_failed), Resource.String.alert_title_error);
            }
            else
            {
                if (ActivityCommon.SelectedManufacturer != ActivityCommon.ManufacturerType.Bmw &&
                    ((ecuInfo.MwTabList == null) || (ecuInfo.MwTabList.Count == 0)))
                {
                    new AlertDialog.Builder(this)
                    .SetMessage(Resource.String.xml_tool_no_mwtab)
                    .SetTitle(Resource.String.alert_title_info)
                    .SetNeutralButton(Resource.String.button_ok, (s, e) =>
                    {
                        TranslateAndSelectJobs(ecuInfo);
                    })
                    .Show();
                    return;
                }
                TranslateAndSelectJobs(ecuInfo);
            }
        }

        private void TranslateAndSelectJobs(EcuInfo ecuInfo)
        {
            _ecuListTranslated = false;
            if (!TranslateEcuText((sender, args) =>
            {
                SelectJobs(ecuInfo);
            }))
            {
                SelectJobs(ecuInfo);
            }
        }

        private string GetReadCommand(EcuInfo ecuInfo)
        {
            return ecuInfo.Sgbd.Contains("1281") ? "WertEinmalLesen" : "LESEN";
        }

        private void SelectMwTabFromListInfo(List<string> fileNames, MwTabFileSelected handler)
        {
            bool handlerCalled = false;
            AlertDialog.Builder builder = new AlertDialog.Builder(this);
            builder.SetMessage(Resource.String.xml_tool_sel_mwtab_info);
            builder.SetTitle(Resource.String.alert_title_info);
            builder.SetPositiveButton(Resource.String.button_ok, (s, e) =>
            {
                handlerCalled = true;
                SelectMwTabFromList(fileNames, handler);
            });
            builder.SetNegativeButton(Resource.String.button_abort, (s, e) =>
            {
                handlerCalled = true;
                handler(null);
            });
            AlertDialog alertDialog = builder.Show();
            alertDialog.DismissEvent += (sender, args) =>
            {
                if (!handlerCalled)
                {
                    handler(null);
                }
            };
        }

        private void SelectMwTabFromList(List<string> fileNames, MwTabFileSelected handler)
        {
            AlertDialog.Builder builder = new AlertDialog.Builder(this);
            builder.SetTitle(Resource.String.xml_tool_select_ecu_type);
            ListView listView = new ListView(this);
            bool handlerCalled = false;

            List<string> displayNames = fileNames.Select(Path.GetFileNameWithoutExtension).ToList();
            ArrayAdapter<string> adapter = new ArrayAdapter<string>(this,
                Android.Resource.Layout.SimpleListItemSingleChoice, displayNames);
            listView.Adapter = adapter;
            listView.ChoiceMode = ChoiceMode.Single;
            listView.SetItemChecked(0, true);
            builder.SetView(listView);
            builder.SetPositiveButton(Resource.String.button_ok, (sender, args) =>
            {
                string fileName = fileNames[listView.CheckedItemPosition];
                handlerCalled = true;
                handler(fileName);
            });
            builder.SetNegativeButton(Resource.String.button_abort, (sender, args) =>
            {
                handlerCalled = true;
                handler(null);
            });
            AlertDialog alertDialog = builder.Show();
            alertDialog.DismissEvent += (sender, args) =>
            {
                if (!handlerCalled)
                {
                    handler(null);
                }
            };
        }

        private List<string> GetBestMatchingMwTab(EcuInfo ecuInfo, Android.App.ProgressDialog progress)
        {
            List<ActivityCommon.MwTabFileEntry> wmTabList = ActivityCommon.GetMatchingVagMwTabs(Path.Combine(_datUkdDir, "mwtabs"), ecuInfo.Sgbd);
            SortedSet<int> mwBlocks = ActivityCommon.ExtractVagMwBlocks(wmTabList);
            string readCommand = GetReadCommand(ecuInfo);
#if false
            {
                StringBuilder sr = new StringBuilder();
                sr.Append(string.Format("-s \"{0}\"", ecuInfo.Sgbd));
                foreach (int block in mwBlocks)
                {
                    sr.Append(string.Format(" -j \"Messwerteblock_lesen#{0};{1}\"", block, readCommand));
                }
                Log.Debug("MwTab", sr.ToString());
            }
#endif
            Dictionary<int, string> unitDict = new Dictionary<int, string>();

            try
            {
                int blockIndex = 0;
                foreach (int block in mwBlocks)
                {
                    if (_ediabasJobAbort)
                    {
                        return null;
                    }
                    int localBlockIndex = blockIndex;
                    RunOnUiThread(() =>
                    {
                        if (progress != null)
                        {
                            progress.Progress = localBlockIndex * 100 / mwBlocks.Count;
                        }
                    });
                    _ediabas.ArgString = string.Format("{0};{1}", block, readCommand);
                    _ediabas.ArgBinaryStd = null;
                    _ediabas.ResultsRequests = string.Empty;
                    _ediabas.ExecuteJob("Messwerteblock_lesen");

                    List<Dictionary<string, EdiabasNet.ResultData>> resultSets = _ediabas.ResultSets;
                    if (resultSets != null && resultSets.Count >= 2)
                    {
                        int dictIndex = 0;
                        foreach (Dictionary<string, EdiabasNet.ResultData> resultDict in resultSets)
                        {
                            if (dictIndex == 0)
                            {
                                dictIndex++;
                                continue;
                            }
                            EdiabasNet.ResultData resultData;
                            string unitText = string.Empty;
                            if (resultDict.TryGetValue("MWEINH_TEXT", out resultData))
                            {
                                unitText = resultData.OpData as string ?? string.Empty;
                            }
                            if (!string.IsNullOrWhiteSpace(unitText))
                            {
                                if (unitText.ToLowerInvariant().StartsWith("mon"))
                                {
                                    unitText = "m/s�";
                                }
                                int key = (block << 16) + dictIndex;
                                if (!unitDict.ContainsKey(key))
                                {
                                    unitDict.Add(key, unitText);
                                }
                            }
                            dictIndex++;
                        }
                    }
                    blockIndex++;
                }
            }
            catch (Exception)
            {
                return null;
            }

            foreach (ActivityCommon.MwTabFileEntry mwTabFileEntry in wmTabList)
            {
                int compareCount = 0;
                int matchCount = 0;
                //Log.Debug("Match", "File: " + mwTabFileEntry.FileName);
                foreach (ActivityCommon.MwTabEntry mwTabEntry in mwTabFileEntry.MwTabList)
                {
                    int key = (mwTabEntry.BlockNumber << 16) + mwTabEntry.ValueIndex;
                    string unitText;
                    if (unitDict.TryGetValue(key, out unitText))
                    {
                        compareCount++;
                        if (mwTabEntry.ValueUnit.ToLowerInvariant().Contains(unitText.ToLowerInvariant()))
                        {
                            //Log.Debug("Match", "Match: '" + unitText + "' '" + mwTabEntry.ValueUnit + "'");
                            matchCount++;
                        }
                        else
                        {
                            //Log.Debug("Match", "Mismatch: '" + unitText + "' '" + mwTabEntry.ValueUnit + "'");
                        }
                    }
                }
                mwTabFileEntry.CompareCount = compareCount;
                mwTabFileEntry.MatchCount = matchCount;
                if (compareCount > 0)
                {
                    mwTabFileEntry.MatchRatio = matchCount * ActivityCommon.MwTabFileEntry.MaxMatchRatio / compareCount;
                }
                else
                {
                    mwTabFileEntry.MatchRatio = (mwTabFileEntry.MwTabList.Count == 0) ? ActivityCommon.MwTabFileEntry.MaxMatchRatio : 0;
                }
            }
            List<ActivityCommon.MwTabFileEntry> wmTabListSorted = wmTabList.OrderByDescending(x => x).ToList();
            if (wmTabListSorted.Count == 0)
            {
                return new List<string>();
            }
            return wmTabListSorted.
                TakeWhile(mwTabFileEntry =>mwTabFileEntry.MatchRatio == wmTabListSorted[0].MatchRatio && mwTabFileEntry.MatchCount >= wmTabListSorted[0].MatchCount / 2).
                Select(mwTabFileEntry => mwTabFileEntry.FileName).ToList();
        }

        private void ExecuteUpdateEcuInfo()
        {
            EdiabasOpen();

            UpdateDisplay();
            if ((_ecuList.Count == 0) || (_activityCommon.SelectedInterface == ActivityCommon.InterfaceType.None))
            {
                return;
            }

            Android.App.ProgressDialog progress = new Android.App.ProgressDialog(this);
            progress.SetCancelable(false);
            progress.SetMessage(GetString(Resource.String.xml_tool_analyze));
            progress.Show();

            bool readFailed = false;
            _ediabasJobAbort = false;
            _jobThread = new Thread(() =>
            {
                List<ActivityCommon.VagEcuEntry> ecuVagList = null;
                Dictionary<int, string> ecuNameDict = null;
                if (ActivityCommon.SelectedManufacturer != ActivityCommon.ManufacturerType.Bmw)
                {
                    ecuVagList = ActivityCommon.ReadVagEcuList(_ecuDir);
                    ecuNameDict = ActivityCommon.GetVagEcuNamesDict(_ecuDir);
                }
                for (int idx = 0; idx < _ecuList.Count; idx++)
                {
                    EcuInfo ecuInfo = _ecuList[idx];
                    if (ecuInfo.Address >= 0)
                    {
                        continue;
                    }
                    try
                    {
                        _ediabas.ResolveSgbdFile(ecuInfo.Sgbd);

                        _ediabas.ArgString = string.Empty;
                        _ediabas.ArgBinaryStd = null;
                        _ediabas.ResultsRequests = string.Empty;
                        _ediabas.NoInitForVJobs = true;
                        _ediabas.ExecuteJob("_VERSIONINFO");

                        if (ActivityCommon.SelectedManufacturer == ActivityCommon.ManufacturerType.Bmw)
                        {
                            StringBuilder stringBuilderComment = new StringBuilder();
                            List<Dictionary<string, EdiabasNet.ResultData>> resultSets = _ediabas.ResultSets;
                            if (resultSets != null && resultSets.Count >= 2)
                            {
                                int dictIndex = 0;
                                foreach (Dictionary<string, EdiabasNet.ResultData> resultDict in resultSets)
                                {
                                    if (dictIndex == 0)
                                    {
                                        dictIndex++;
                                        continue;
                                    }
                                    for (int i = 0; ; i++)
                                    {
                                        EdiabasNet.ResultData resultData;
                                        if (resultDict.TryGetValue("ECUCOMMENT" + i.ToString(Culture), out resultData))
                                        {
                                            if (resultData.OpData is string)
                                            {
                                                if (stringBuilderComment.Length > 0)
                                                {
                                                    stringBuilderComment.Append(";");
                                                }
                                                stringBuilderComment.Append((string)resultData.OpData);
                                            }
                                        }
                                        else
                                        {
                                            break;
                                        }
                                    }
                                    dictIndex++;
                                }
                            }
                            ecuInfo.Description = stringBuilderComment.ToString();
                        }
                        string ecuName = Path.GetFileNameWithoutExtension(_ediabas.SgbdFileName) ?? string.Empty;
                        if (_ecuList.Any(info => !info.Equals(ecuInfo) && string.Compare(info.Sgbd, ecuName, StringComparison.OrdinalIgnoreCase) == 0))
                        {   // already existing
                            _ecuList.Remove(ecuInfo);
                            continue;
                        }
                        string displayName = ecuName;
                        if (ActivityCommon.SelectedManufacturer != ActivityCommon.ManufacturerType.Bmw && ecuVagList != null && ecuNameDict != null)
                        {
                            string compareName = ecuInfo.Sgbd;
                            bool limitString = false;
                            if ((string.Compare(ecuInfo.Sgbd, ecuName, StringComparison.OrdinalIgnoreCase) == 0) &&
                                (compareName.Length > 3))
                            {   // no group file selected
                                compareName = compareName.Substring(0, 3);
                                limitString = true;
                            }
                            foreach (ActivityCommon.VagEcuEntry entry in ecuVagList)
                            {
                                string entryName = entry.SysName;
                                if (limitString && entryName.Length > 3)
                                {
                                    entryName = entryName.Substring(0, 3);
                                }
                                if (string.Compare(entryName, compareName, StringComparison.OrdinalIgnoreCase) == 0)
                                {
                                    ecuNameDict.TryGetValue(entry.Address, out displayName);
                                    break;
                                }
                            }
                        }
                        ecuInfo.Name = ecuName.ToUpperInvariant();
                        if (string.IsNullOrEmpty(ecuInfo.PageName) && string.IsNullOrEmpty(ecuInfo.EcuName))
                        {
                            ecuInfo.PageName = displayName;
                            ecuInfo.EcuName = displayName;
                        }
                        ecuInfo.Sgbd = ecuName;
                        ecuInfo.Address = 0;
                    }
                    catch (Exception)
                    {
                        readFailed = true;
                    }
                }

                RunOnUiThread(() =>
                {
                    progress.Hide();
                    progress.Dispose();

                    _ecuListTranslated = false;
                    SupportInvalidateOptionsMenu();
                    UpdateDisplay();
                    if (readFailed)
                    {
                        _commErrorsOccured = true;
                        _activityCommon.ShowAlert(GetString(Resource.String.xml_tool_read_ecu_info_failed), Resource.String.alert_title_error);
                    }
                });
            });
            _jobThread.Start();
        }

        private void EcuCheckChanged(EcuInfo ecuInfo)
        {
            if (ecuInfo.Selected)
            {
                ExecuteJobsRead(ecuInfo);
            }
            else
            {
                int selectCount = 0;
                if (ecuInfo.JobList != null)
                {
                    selectCount = ecuInfo.JobList.Count(job => job.Selected);
                }
                if ((selectCount > 0) || !string.IsNullOrEmpty(ecuInfo.MwTabFileName))
                {
                    new AlertDialog.Builder(this)
                        .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                        {
                            ecuInfo.JobList = null;
                            ecuInfo.MwTabFileName = string.Empty;
                            ecuInfo.MwTabList = null;
                            ecuInfo.IgnoreXmlFile = true;
                            UpdateDisplay();
                        })
                        .SetNegativeButton(Resource.String.button_no, (sender, args) => { })
                        .SetMessage(Resource.String.xml_tool_reset_ecu_setting)
                        .SetTitle(Resource.String.alert_title_question)
                        .Show();
                }
            }
            UpdateDisplay();
        }

        private bool AbortEdiabasJob()
        {
            if (_ediabasJobAbort)
            {
                return true;
            }
            return false;
        }

        private void BroadcastReceived(Context context, Intent intent)
        {
            if (intent == null)
            {   // from usb check timer
                return;
            }
            string action = intent.Action;
            switch (action)
            {
                case UsbManager.ActionUsbDeviceDetached:
                    EdiabasClose();
                    break;
            }
        }

        private void ReadPageXml(EcuInfo ecuInfo, XDocument document)
        {
            if (document.Root == null)
            {
                return;
            }
            XNamespace ns = document.Root.GetDefaultNamespace();
            XElement pageNode = document.Root.Element(ns + "page");
            if (pageNode == null)
            {
                return;
            }
            XElement stringsNode = GetDefaultStringsNode(ns, pageNode);
            XElement jobsNode = pageNode.Element(ns + "jobs");
            if (jobsNode == null)
            {
                return;
            }

            if (stringsNode != null)
            {
                string pageName = GetStringEntry(DisplayNamePage, ns, stringsNode);
                if (pageName != null)
                {
                    ecuInfo.PageName = pageName;
                }
            }

            foreach (XmlToolEcuActivity.JobInfo job in ecuInfo.JobList)
            {
                XElement jobNode = GetJobNode(job, ns, jobsNode);
                if (jobNode != null)
                {
                    job.Selected = true;
                    foreach (XmlToolEcuActivity.ResultInfo result in job.Results)
                    {
                        if (result.MwTabEntry != null)
                        {
                            jobNode = GetJobNode(job, ns, jobsNode, XmlToolEcuActivity.GetJobArgs(result.MwTabEntry, ecuInfo));
                            if (jobNode == null)
                            {
                                continue;
                            }
                        }
                        XElement displayNode = GetDisplayNode(result, ns, jobNode);
                        if (displayNode != null)
                        {
                            result.Selected = true;
                            XAttribute formatAttr = displayNode.Attribute("format");
                            if (formatAttr != null)
                            {
                                result.Format = formatAttr.Value;
                            }
                            XAttribute logTagAttr = displayNode.Attribute("log_tag");
                            if (logTagAttr != null)
                            {
                                result.LogTag = logTagAttr.Value;
                            }
                        }
                        if (stringsNode != null)
                        {
                            string displayTag = DisplayNameJobPrefix + job.Name + "#" + result.Name;
                            string displayText = GetStringEntry(displayTag, ns, stringsNode);
                            if (displayText != null)
                            {
                                result.DisplayText = displayText;
                            }
                        }
                    }
                }
            }
        }

        private string ReadPageSgbd(XDocument document, out string mwTabFileName)
        {
            mwTabFileName = null;
            if (document.Root == null)
            {
                return null;
            }
            XNamespace ns = document.Root.GetDefaultNamespace();
            XElement pageNode = document.Root.Element(ns + "page");
            if (pageNode == null)
            {
                return null;
            }
            XElement jobsNode = pageNode.Element(ns + "jobs");
            XAttribute sgbdAttr = jobsNode?.Attribute("sgbd");
            XAttribute mwtabAttr = jobsNode?.Attribute("mwtab");
            if (mwtabAttr != null)
            {
                mwTabFileName = Path.Combine(_ecuDir, mwtabAttr.Value);
            }
            return sgbdAttr?.Value;
        }

        private XDocument GeneratePageXml(EcuInfo ecuInfo, XDocument documentOld)
        {
            try
            {
                if (ecuInfo.JobList == null)
                {
                    return null;
                }
                XDocument document = documentOld;
                if (document?.Root == null)
                {
                    document = XDocument.Parse(string.Format(XmlDocumentFrame, "fragment"));
                }
                if (document.Root == null)
                {
                    return null;
                }
                XNamespace ns = document.Root.GetDefaultNamespace();
                XElement pageNode = document.Root.Element(ns + "page");
                if (pageNode == null)
                {
                    pageNode = new XElement(ns + "page");
                    document.Root.Add(pageNode);
                }
                XAttribute pageNameAttr = pageNode.Attribute("name");
                if (pageNameAttr == null)
                {
                    pageNode.Add(new XAttribute("name", DisplayNamePage));
                }
                XAttribute pageLogFileAttr = pageNode.Attribute("logfile");
                if (pageLogFileAttr == null)
                {
                    pageNode.Add(new XAttribute("logfile", ActivityCommon.CreateValidFileName(ecuInfo.Name + ".log")));
                }

                XElement stringsNode = GetDefaultStringsNode(ns, pageNode);
                if (stringsNode == null)
                {
                    stringsNode = new XElement(ns + "strings");
                    pageNode.Add(stringsNode);
                }
                else
                {
                    RemoveGeneratedStringEntries(ns, stringsNode);
                }

                XElement stringNodePage = new XElement(ns + "string", ecuInfo.PageName);
                stringNodePage.Add(new XAttribute("name", DisplayNamePage));
                stringsNode.Add(stringNodePage);

                XElement jobsNodeOld = pageNode.Element(ns + "jobs");
                XElement jobsNodeNew = new XElement(ns + "jobs");
                if (jobsNodeOld != null)
                {
                    jobsNodeNew.ReplaceAttributes(from el in jobsNodeOld.Attributes() where (el.Name != "sgbd" && el.Name != "mwtab") select new XAttribute(el));
                }

                jobsNodeNew.Add(new XAttribute("sgbd", ecuInfo.Sgbd));
                if (!string.IsNullOrEmpty(ecuInfo.MwTabFileName))
                {
                    string relativePath = ActivityCommon.MakeRelativePath(_ecuDir, ecuInfo.MwTabFileName);
                    if (!string.IsNullOrEmpty(relativePath))
                    {
                        jobsNodeNew.Add(new XAttribute("mwtab", relativePath));
                    }
                }

                foreach (XmlToolEcuActivity.JobInfo job in ecuInfo.JobList)
                {
                    if (!job.Selected)
                    {
                        continue;
                    }
                    XElement jobNodeOld = null;
                    XElement jobNodeNew = new XElement(ns + "job");
                    if (jobsNodeOld != null)
                    {
                        jobNodeOld = GetJobNode(job, ns, jobsNodeOld);
                        if (jobNodeOld != null)
                        {
                            jobNodeNew.ReplaceAttributes(from el in jobNodeOld.Attributes() where el.Name != "name" select new XAttribute(el));
                        }
                    }

                    jobNodeNew.Add(new XAttribute("name", job.Name));

                    int jobId = 1;
                    int lastBlockNumber = -1;
                    foreach (XmlToolEcuActivity.ResultInfo result in job.Results)
                    {
                        if (!result.Selected)
                        {
                            continue;
                        }
                        if (result.MwTabEntry != null)
                        {
                            if (lastBlockNumber != result.MwTabEntry.BlockNumber)
                            {
                                if (lastBlockNumber >= 0)
                                {
                                    // store last generated job
                                    jobNodeOld?.Remove();
                                    jobsNodeNew.Add(jobNodeNew);
                                }

                                string args = XmlToolEcuActivity.GetJobArgs(result.MwTabEntry, ecuInfo);
                                jobNodeOld = null;
                                jobNodeNew = new XElement(ns + "job");
                                if (jobsNodeOld != null)
                                {
                                    jobNodeOld = GetJobNode(job, ns, jobsNodeOld, args);
                                    if (jobNodeOld != null)
                                    {
                                        jobNodeNew.ReplaceAttributes(from el in jobNodeOld.Attributes() where (el.Name != "id" && el.Name != "name" && el.Name != "args") select new XAttribute(el));
                                    }
                                }

                                jobNodeNew.Add(new XAttribute("id", (jobId++).ToString(Culture)));
                                jobNodeNew.Add(new XAttribute("name", job.Name));
                                jobNodeNew.Add(new XAttribute("args", args));
                                lastBlockNumber = result.MwTabEntry.BlockNumber;
                            }
                        }

                        XElement displayNodeOld = null;
                        XElement displayNodeNew = new XElement(ns + "display");
                        if (jobNodeOld != null)
                        {
                            displayNodeOld = GetDisplayNode(result, ns, jobNodeOld);
                            if (displayNodeOld != null)
                            {
                                displayNodeNew.ReplaceAttributes(from el in displayNodeOld.Attributes()
                                                                 where el.Name != "result" && el.Name != "format" && el.Name != "log_tag"
                                                                 select new XAttribute(el));
                            }
                        }
                        XAttribute nameAttr = displayNodeNew.Attribute("name");
                        if (nameAttr != null)
                        {
                            if (nameAttr.Value.StartsWith(DisplayNameJobPrefix, StringComparison.Ordinal))
                            {
                                nameAttr.Remove();
                            }
                        }

                        string displayTag = DisplayNameJobPrefix + job.Name + "#" + result.Name;
                        if (displayNodeNew.Attribute("name") == null)
                        {
                            displayNodeNew.Add(new XAttribute("name", displayTag));
                        }
                        string resultName = result.Name;
                        if (result.MwTabEntry != null)
                        {
                            resultName = string.Format(Culture, "{0}#MW_Wert", result.MwTabEntry.ValueIndex);
                        }
                        displayNodeNew.Add(new XAttribute("result", resultName));
                        displayNodeNew.Add(new XAttribute("format", result.Format));
                        if (!string.IsNullOrEmpty(result.LogTag))
                        {
                            displayNodeNew.Add(new XAttribute("log_tag", result.LogTag));
                        }

                        XElement stringNode = new XElement(ns + "string", result.DisplayText);
                        stringNode.Add(new XAttribute("name", displayTag));
                        stringsNode.Add(stringNode);

                        displayNodeOld?.Remove();
                        jobNodeNew.Add(displayNodeNew);
                    }
                    jobNodeOld?.Remove();
                    jobsNodeNew.Add(jobNodeNew);
                }
                jobsNodeOld?.Remove();
                pageNode.Add(jobsNodeNew);

                return document;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void ReadErrorsXml(XDocument document)
        {
            if (document.Root == null)
            {
                return;
            }
            XNamespace ns = document.Root.GetDefaultNamespace();
            XElement pageNode = document.Root.Element(ns + "page");
            if (pageNode == null)
            {
                return;
            }
            XElement stringsNode = GetDefaultStringsNode(ns, pageNode);
            XElement errorsNode = pageNode.Element(ns + "read_errors");
            if (errorsNode == null)
            {
                return;
            }

            foreach (EcuInfo ecuInfo in _ecuList)
            {
                XElement ecuNode = GetEcuNode(ecuInfo, ns, errorsNode);
                if (ecuNode != null)
                {
                    if (stringsNode != null)
                    {
                        string displayTag = DisplayNameEcuPrefix + ecuInfo.Name;
                        string displayText = GetStringEntry(displayTag, ns, stringsNode);
                        if (displayText != null)
                        {
                            ecuInfo.EcuName = displayText;
                        }
                    }
                }
            }
            if (_manualConfigIdx > 0)
            {   // manual mode, add missing entries
                foreach (XElement ecuNode in errorsNode.Elements(ns + "ecu"))
                {
                    XAttribute sgbdAttr = ecuNode.Attribute("sgbd");
                    string sgbdName = sgbdAttr?.Value;
                    if (string.IsNullOrEmpty(sgbdName))
                    {
                        continue;
                    }
                    bool ecuFound = _ecuList.Any(ecuInfo => string.Compare(sgbdName, ecuInfo.Sgbd, StringComparison.OrdinalIgnoreCase) == 0);
                    if (!ecuFound)
                    {
                        EcuInfo ecuInfo = new EcuInfo(sgbdName.ToUpperInvariant(), -1, string.Empty, sgbdName, string.Empty, string.Empty)
                        {
                            PageName = string.Empty,
                            EcuName = string.Empty
                        };
                        _ecuList.Add(ecuInfo);
                    }
                }
            }
        }

        private XDocument GenerateErrorsXml(XDocument documentOld)
        {
            try
            {
                XDocument document = documentOld;
                if (document?.Root == null)
                {
                    document = XDocument.Parse(string.Format(XmlDocumentFrame, "fragment"));
                }
                if (document.Root == null)
                {
                    return null;
                }
                XNamespace ns = document.Root.GetDefaultNamespace();
                XElement pageNode = document.Root.Element(ns + "page");
                if (pageNode == null)
                {
                    pageNode = new XElement(ns + "page");
                    document.Root.Add(pageNode);
                }
                XAttribute pageNameAttr = pageNode.Attribute("name");
                if (pageNameAttr == null)
                {
                    pageNode.Add(new XAttribute("name", DisplayNamePage));
                }

                XElement stringsNode = GetDefaultStringsNode(ns, pageNode);
                if (stringsNode == null)
                {
                    stringsNode = new XElement(ns + "strings");
                    pageNode.Add(stringsNode);
                }
                else
                {
                    RemoveGeneratedStringEntries(ns, stringsNode);
                }

                XElement stringNodePage = new XElement(ns + "string", GetString(Resource.String.xml_tool_errors_page));
                stringNodePage.Add(new XAttribute("name", DisplayNamePage));
                stringsNode.Add(stringNodePage);

                XElement errorsNodeOld = pageNode.Element(ns + "read_errors");
                XElement errorsNodeNew = new XElement(ns + "read_errors");
                if (errorsNodeOld != null)
                {
                    errorsNodeNew.ReplaceAttributes(from el in errorsNodeOld.Attributes() select new XAttribute(el));
                }

                foreach (EcuInfo ecuInfo in _ecuList)
                {
                    XElement ecuNode = null;
                    if (errorsNodeOld != null)
                    {
                        ecuNode = GetEcuNode(ecuInfo, ns, errorsNodeOld);
                        if (ecuNode != null)
                        {
                            ecuNode = new XElement(ecuNode);
                        }
                    }
                    if (ecuNode == null)
                    {
                        ecuNode = new XElement(ns + "ecu");
                    }
                    else
                    {
                        XAttribute attr = ecuNode.Attribute("name");
                        attr?.Remove();
                        attr = ecuNode.Attribute("sgbd");
                        attr?.Remove();
                    }
                    string displayTag = DisplayNameEcuPrefix + ecuInfo.Name;
                    errorsNodeNew.Add(ecuNode);
                    ecuNode.Add(new XAttribute("name", displayTag));
                    ecuNode.Add(new XAttribute("sgbd", ecuInfo.Sgbd));

                    XElement stringNode = new XElement(ns + "string", ecuInfo.EcuName);
                    stringNode.Add(new XAttribute("name", displayTag));
                    stringsNode.Add(stringNode);
                }
                errorsNodeOld?.Remove();
                pageNode.Add(errorsNodeNew);

                return document;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void ReadPagesXml(XDocument document)
        {
            string xmlFileDir = XmlFileDir();
            if (xmlFileDir == null)
            {
                return;
            }
            if (document.Root == null)
            {
                return;
            }
            XNamespace ns = document.Root.GetDefaultNamespace();
            XElement pagesNode = document.Root.Element(ns + "pages");
            if (pagesNode == null)
            {
                return;
            }

            if (_manualConfigIdx > 0)
            {   // manual mode, create ecu list
                _ecuList.Clear();
                _ecuListTranslated = false;
                foreach (XElement node in pagesNode.Elements(ns + "include"))
                {
                    XAttribute fileAttrib = node.Attribute("filename");
                    if (fileAttrib == null)
                    {
                        continue;
                    }
                    string fileName = fileAttrib.Value;
                    if (string.Compare(fileName, ErrorsFileName, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        continue;
                    }
                    string xmlPageFile = Path.Combine(xmlFileDir, fileName);
                    if (!File.Exists(xmlPageFile))
                    {
                        continue;
                    }
                    string ecuName = Path.GetFileNameWithoutExtension(fileName);
                    if (string.IsNullOrEmpty(ecuName))
                    {
                        continue;
                    }
                    try
                    {
                        string mwTabFileName;
                        string sgbdName = ReadPageSgbd(XDocument.Load(xmlPageFile), out mwTabFileName);
                        if (!string.IsNullOrEmpty(sgbdName))
                        {
                            _ecuList.Add(new EcuInfo(ecuName, -1, string.Empty, sgbdName, string.Empty, mwTabFileName)
                            {
                                Selected = true
                            });
                        }
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }
            }
            else
            {   // auto mode, reorder list and set selections, add missing entries
                foreach (XElement node in pagesNode.Elements(ns + "include").Reverse())
                {
                    XAttribute fileAttrib = node.Attribute("filename");
                    if (fileAttrib == null)
                    {
                        continue;
                    }
                    string fileName = fileAttrib.Value;
                    if (string.Compare(fileName, ErrorsFileName, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        continue;
                    }
                    string xmlPageFile = Path.Combine(xmlFileDir, fileName);
                    if (!File.Exists(xmlPageFile))
                    {
                        continue;
                    }
                    bool found = false;
                    for (int i = 0; i < _ecuList.Count; i++)
                    {
                        EcuInfo ecuInfo = _ecuList[i];
                        string ecuFileName = ActivityCommon.CreateValidFileName(ecuInfo.Name + PageExtension);
                        if (string.Compare(ecuFileName, fileName, StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            found = true;
                            ecuInfo.Selected = true;
                            _ecuList.Remove(ecuInfo);
                            _ecuList.Insert(0, ecuInfo);
                            break;
                        }
                    }
                    if (!found)
                    {
                        string ecuName = Path.GetFileNameWithoutExtension(fileName);
                        if (!string.IsNullOrEmpty(ecuName))
                        {
                            try
                            {
                                string mwTabFileName;
                                string sgbdName = ReadPageSgbd(XDocument.Load(xmlPageFile), out mwTabFileName);
                                if (!string.IsNullOrEmpty(sgbdName))
                                {
                                    _ecuList.Insert(0, new EcuInfo(ecuName, -1, string.Empty, sgbdName, string.Empty, mwTabFileName)
                                    {
                                        Selected = true
                                    });
                                }
                            }
                            catch (Exception)
                            {
                                // ignored
                            }
                        }
                    }
                }
            }

            {
                string fileName = ErrorsFileName;
                XElement fileNode = GetFileNode(fileName, ns, pagesNode);
                if ((fileNode == null) || !File.Exists(Path.Combine(xmlFileDir, fileName)))
                {
                    _addErrorsPage = false;
                }
            }
        }

        private XDocument GeneratePagesXml(XDocument documentOld)
        {
            string xmlFileDir = XmlFileDir();
            if (xmlFileDir == null)
            {
                return null;
            }
            try
            {
                XDocument document = documentOld;
                if (document?.Root == null)
                {
                    document = XDocument.Parse(string.Format(XmlDocumentFrame, "fragment"));
                }
                if (document.Root == null)
                {
                    return null;
                }
                XNamespace ns = document.Root.GetDefaultNamespace();
                XElement pagesNodeOld = document.Root.Element(ns + "pages");
                XElement pagesNodeNew = new XElement(ns + "pages");
                if (pagesNodeOld != null)
                {
                    pagesNodeNew.ReplaceAttributes(from el in pagesNodeOld.Attributes() select new XAttribute(el));
                }

                foreach (EcuInfo ecuInfo in _ecuList)
                {
                    string fileName = ActivityCommon.CreateValidFileName(ecuInfo.Name + PageExtension);
                    if (!ecuInfo.Selected || !File.Exists(Path.Combine(xmlFileDir, fileName)))
                    {
                        continue;
                    }
                    XElement fileNode = null;
                    if (pagesNodeOld != null)
                    {
                        fileNode = GetFileNode(fileName, ns, pagesNodeOld);
                        if (fileNode != null)
                        {
                            fileNode = new XElement(fileNode);
                        }
                    }
                    if (fileNode == null)
                    {
                        fileNode = new XElement(ns + "include");
                    }
                    else
                    {
                        XAttribute attr = fileNode.Attribute("filename");
                        attr?.Remove();
                    }

                    fileNode.Add(new XAttribute("filename", fileName));
                    pagesNodeNew.Add(fileNode);
                }

                {
                    // errors file
                    string fileName = ErrorsFileName;
                    if (_addErrorsPage && File.Exists(Path.Combine(xmlFileDir, fileName)))
                    {
                        XElement fileNode = null;
                        if (pagesNodeOld != null)
                        {
                            fileNode = GetFileNode(fileName, ns, pagesNodeOld);
                            if (fileNode != null)
                            {
                                fileNode = new XElement(fileNode);
                            }
                        }
                        if (fileNode == null)
                        {
                            fileNode = new XElement(ns + "include");
                        }
                        else
                        {
                            XAttribute attr = fileNode.Attribute("filename");
                            attr?.Remove();
                        }
                        fileNode.Add(new XAttribute("filename", fileName));
                        pagesNodeNew.Add(fileNode);
                    }
                }
                pagesNodeOld?.Remove();
                document.Root.Add(pagesNodeNew);

                return document;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private XDocument GenerateConfigXml(XDocument documentOld)
        {
            string xmlFileDir = XmlFileDir();
            if (xmlFileDir == null)
            {
                return null;
            }
            try
            {
                XDocument document = documentOld;
                if (document?.Root == null)
                {
                    document = XDocument.Parse(string.Format(XmlDocumentFrame, "configuration"));
                }
                if (document.Root == null)
                {
                    return null;
                }
                XNamespace ns = document.Root.GetDefaultNamespace();
                XElement globalNode = document.Root.Element(ns + "global");
                if (globalNode == null)
                {
                    globalNode = new XElement(ns + "global");
                    document.Root.Add(globalNode);
                }
                else
                {
                    XAttribute attr = globalNode.Attribute("ecu_path");
                    attr?.Remove();
                    attr = globalNode.Attribute("manufacturer");
                    attr?.Remove();
                    attr = globalNode.Attribute("interface");
                    attr?.Remove();
                }

                XAttribute logPathAttr = globalNode.Attribute("log_path");
                if (logPathAttr == null)
                {
                    globalNode.Add(new XAttribute("log_path", "Log"));
                }

                string manufacturerName = string.Empty;
                switch (ActivityCommon.SelectedManufacturer)
                {
                    case ActivityCommon.ManufacturerType.Bmw:
                        manufacturerName = "BMW";
                        break;

                    case ActivityCommon.ManufacturerType.Audi:
                        manufacturerName = "Audi";
                        break;

                    case ActivityCommon.ManufacturerType.Seat:
                        manufacturerName = "Seat";
                        break;

                    case ActivityCommon.ManufacturerType.Skoda:
                        manufacturerName = "Skoda";
                        break;

                    case ActivityCommon.ManufacturerType.Vw:
                        manufacturerName = "VW";
                        break;
                }
                globalNode.Add(new XAttribute("manufacturer", manufacturerName));

                string interfaceName = string.Empty;
                switch (_activityCommon.SelectedInterface)
                {
                    case ActivityCommon.InterfaceType.Bluetooth:
                        interfaceName = "BLUETOOTH";
                        break;

                    case ActivityCommon.InterfaceType.Enet:
                        interfaceName = "ENET";
                        break;

                    case ActivityCommon.InterfaceType.ElmWifi:
                        interfaceName = "ELMWIFI";
                        break;

                    case ActivityCommon.InterfaceType.Ftdi:
                        interfaceName = "FTDI";
                        break;
                }
                globalNode.Add(new XAttribute("interface", interfaceName));

                XElement includeNode = document.Root.Element(ns + "include");
                if (includeNode == null)
                {
                    includeNode = new XElement(ns + "include");
                    document.Root.Add(includeNode);
                    includeNode.Add(new XAttribute("filename", PagesFileName));
                }
                return document;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void ReadAllXml()
        {
            _addErrorsPage = true;
            string xmlFileDir = XmlFileDir();
            if (xmlFileDir == null)
            {
                return;
            }
            string xmlPagesFile = Path.Combine(xmlFileDir, PagesFileName);
            if (File.Exists(xmlPagesFile))
            {
                try
                {
                    XDocument documentPages = XDocument.Load(xmlPagesFile);
                    ReadPagesXml(documentPages);
                }
                catch (Exception)
                {
                    // ignored
                }
            }
            string xmlErrorsFile = Path.Combine(xmlFileDir, ErrorsFileName);
            if (File.Exists(xmlErrorsFile))
            {
                try
                {
                    XDocument documentPages = XDocument.Load(xmlErrorsFile);
                    ReadErrorsXml(documentPages);
                }
                catch (Exception)
                {
                    // ignored
                }
            }
            _activityCommon.ReadTranslationCache(Path.Combine(xmlFileDir, TranslationFileName));
        }

        private string SaveAllXml()
        {
            string xmlFileDir = XmlFileDir();
            if (xmlFileDir == null)
            {
                return null;
            }
            try
            {
                Directory.CreateDirectory(xmlFileDir);
                // page files
                foreach (EcuInfo ecuInfo in _ecuList)
                {
                    if (ecuInfo.JobList == null) continue;
                    if (!ecuInfo.Selected) continue;
                    string xmlPageFile = Path.Combine(xmlFileDir, ActivityCommon.CreateValidFileName(ecuInfo.Name + PageExtension));
                    XDocument documentPage = null;
                    if (File.Exists(xmlPageFile))
                    {
                        try
                        {
                            documentPage = XDocument.Load(xmlPageFile);
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }
                    XDocument documentPageNew = GeneratePageXml(ecuInfo, documentPage);
                    if (documentPageNew != null)
                    {
                        try
                        {
                            documentPageNew.Save(xmlPageFile);
                        }
                        catch (Exception)
                        {
                            return null;
                        }
                    }
                }

                {
                    // errors file
                    string xmlErrorsFile = Path.Combine(xmlFileDir, ErrorsFileName);
                    XDocument documentPage = null;
                    if (File.Exists(xmlErrorsFile))
                    {
                        try
                        {
                            documentPage = XDocument.Load(xmlErrorsFile);
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }
                    XDocument documentPageNew = GenerateErrorsXml(documentPage);
                    if (documentPageNew != null)
                    {
                        try
                        {
                            documentPageNew.Save(xmlErrorsFile);
                        }
                        catch (Exception)
                        {
                            return null;
                        }
                    }
                }

                // pages file
                string xmlPagesFile = Path.Combine(xmlFileDir, PagesFileName);
                XDocument documentPages = null;
                if (File.Exists(xmlPagesFile))
                {
                    try
                    {
                        documentPages = XDocument.Load(xmlPagesFile);
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }
                XDocument documentPagesNew = GeneratePagesXml(documentPages);
                if (documentPagesNew != null)
                {
                    try
                    {
                        documentPagesNew.Save(xmlPagesFile);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }

                // config file
                string interfaceType = string.Empty;
                switch (_activityCommon.SelectedInterface)
                {
                    case ActivityCommon.InterfaceType.Bluetooth:
                        interfaceType = "Bt";
                        break;

                    case ActivityCommon.InterfaceType.Enet:
                        interfaceType = "Enet";
                        break;

                    case ActivityCommon.InterfaceType.ElmWifi:
                        interfaceType = "ElmWifi";
                        break;

                    case ActivityCommon.InterfaceType.Ftdi:
                        interfaceType = "Ftdi";
                        break;
                }
                string prefix;
                if (_manualConfigIdx > 0)
                {
                    prefix = ManualConfigName + _manualConfigIdx.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    prefix = string.IsNullOrEmpty(_vin) ? UnknownVinConfigName : ActivityCommon.CreateValidFileName(_vin);
                }
                string xmlConfigFile = Path.Combine(xmlFileDir, prefix + "_" + interfaceType + ConfigFileExtension);
                XDocument documentConfig = null;
                if (File.Exists(xmlConfigFile))
                {
                    try
                    {
                        documentConfig = XDocument.Load(xmlConfigFile);
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }
                XDocument documentConfigNew = GenerateConfigXml(documentConfig);
                if (documentConfigNew != null)
                {
                    try
                    {
                        documentConfigNew.Save(xmlConfigFile);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
                ActivityCommon.WriteResourceToFile(typeof(XmlToolActivity).Namespace + ".Xml." + XsdFileName, Path.Combine(xmlFileDir, XsdFileName));
                _activityCommon.StoreTranslationCache(Path.Combine(xmlFileDir, TranslationFileName));
                return xmlConfigFile;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ReSharper disable once UnusedMethodReturnValue.Local
        private bool DeleteAllXml()
        {
            _ecuList.Clear();
            _ecuListTranslated = false;
            UpdateDisplay();
            try
            {
                string xmlFileDir = XmlFileDir();
                if (xmlFileDir == null)
                {
                    return false;
                }
                Directory.Delete(xmlFileDir, true);
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        // ReSharper disable once UnusedMethodReturnValue.Local
        private bool SaveConfiguration(bool finish)
        {
            if (IsJobRunning())
            {
                return false;
            }
            string xmlFileName = SaveAllXml();
            if (xmlFileName == null)
            {
                _activityCommon.ShowAlert(GetString(Resource.String.xml_tool_save_xml_failed), Resource.String.alert_title_error);
                return false;
            }
            if (!finish)
            {
                return true;
            }
            Intent intent = new Intent();
            intent.PutExtra(ExtraFileName, xmlFileName);

            // Set result and finish this Activity
            SetResult(Android.App.Result.Ok, intent);
            FinishContinue();
            return true;
        }

        private XElement GetJobNode(XmlToolEcuActivity.JobInfo job, XNamespace ns, XElement jobsNode)
        {
            return (from node in jobsNode.Elements(ns + "job")
                    let nameAttrib = node.Attribute("name")
                    where nameAttrib != null
                    where string.Compare(nameAttrib.Value, job.Name, StringComparison.OrdinalIgnoreCase) == 0 select node).FirstOrDefault();
        }

        private XElement GetJobNode(XmlToolEcuActivity.JobInfo job, XNamespace ns, XElement jobsNode, string args)
        {
            return (from node in jobsNode.Elements(ns + "job")
                    let nameAttrib = node.Attribute("name")
                    let argsAttrib = node.Attribute("args")
                    where nameAttrib != null
                    where argsAttrib != null
                    where string.Compare(nameAttrib.Value, job.Name, StringComparison.OrdinalIgnoreCase) == 0
                    where string.Compare(argsAttrib.Value, args, StringComparison.OrdinalIgnoreCase) == 0
                    select node).FirstOrDefault();
        }

        private XElement GetDisplayNode(XmlToolEcuActivity.ResultInfo result, XNamespace ns, XElement jobNode)
        {
            if (result.MwTabEntry != null)
            {
                string resultName = string.Format(Culture, "{0}#MW_Wert", result.MwTabEntry.ValueIndex, result.Name);
                return (from node in jobNode.Elements(ns + "display")
                        let nameAttrib = node.Attribute("result")
                        where nameAttrib != null
                        where string.Compare(nameAttrib.Value, resultName, StringComparison.OrdinalIgnoreCase) == 0
                        select node).FirstOrDefault();
            }
            return (from node in jobNode.Elements(ns + "display")
                    let nameAttrib = node.Attribute("result")
                    where nameAttrib != null
                    where string.Compare(nameAttrib.Value, result.Name, StringComparison.OrdinalIgnoreCase) == 0 select node).FirstOrDefault();
        }

        private XElement GetFileNode(string fileName, XNamespace ns, XElement pagesNode)
        {
            return (from node in pagesNode.Elements(ns + "include")
                    let fileAttrib = node.Attribute("filename")
                    where fileAttrib != null
                    where string.Compare(fileAttrib.Value, fileName, StringComparison.OrdinalIgnoreCase) == 0
                    select node).FirstOrDefault();
        }

        private XElement GetEcuNode(EcuInfo ecuInfo, XNamespace ns, XElement errorsNode)
        {
            return (from node in errorsNode.Elements(ns + "ecu")
                    let nameAttrib = node.Attribute("sgbd")
                    where nameAttrib != null
                    where string.Compare(nameAttrib.Value, ecuInfo.Sgbd, StringComparison.OrdinalIgnoreCase) == 0
                    select node).FirstOrDefault();
        }

        private XElement GetDefaultStringsNode(XNamespace ns, XElement pageNode)
        {
            return pageNode.Elements(ns + "strings").FirstOrDefault(node => node.Attribute("lang") == null);
        }

        private string GetStringEntry(string entryName, XNamespace ns, XElement stringsNode)
        {
            return (from node in stringsNode.Elements(ns + "string")
                    let nameAttr = node.Attribute("name")
                    where nameAttr != null
                    where string.Compare(nameAttr.Value, entryName, StringComparison.Ordinal) == 0 select node.FirstNode).OfType<XText>().Select(text => text.Value).FirstOrDefault();
        }

        private void RemoveGeneratedStringEntries(XNamespace ns, XElement stringsNode)
        {
            List<XElement> removeList =
                (from node in stringsNode.Elements(ns + "string")
                    let nameAttr = node.Attribute("name")
                    where nameAttr != null &&
                          (nameAttr.Value.StartsWith(DisplayNameJobPrefix, StringComparison.Ordinal) ||
                           nameAttr.Value.StartsWith(DisplayNameEcuPrefix, StringComparison.Ordinal) ||
                           string.Compare(nameAttr.Value, DisplayNamePage, StringComparison.Ordinal) == 0)
                    select node).ToList();
            foreach (XElement node in removeList)
            {
                node.Remove();
            }
        }

        private string XmlFileDir()
        {
            if (string.IsNullOrEmpty(_appDataDir))
            {
                return null;
            }
            string configBaseDir = Path.Combine(_appDataDir, "Configurations");
            switch (ActivityCommon.SelectedManufacturer)
            {
                case ActivityCommon.ManufacturerType.Audi:
                    configBaseDir += "Audi";
                    break;

                case ActivityCommon.ManufacturerType.Seat:
                    configBaseDir += "Seat";
                    break;

                case ActivityCommon.ManufacturerType.Skoda:
                    configBaseDir += "Skoda";
                    break;

                case ActivityCommon.ManufacturerType.Vw:
                    configBaseDir += "Vw";
                    break;
            }
            string vin = _vin;
            if (_manualConfigIdx > 0)
            {
                vin = ManualConfigName + _manualConfigIdx.ToString(CultureInfo.InvariantCulture);
            }
            if (string.IsNullOrEmpty(vin))
            {
                vin = UnknownVinConfigName;
            }
            try
            {
                return Path.Combine(configBaseDir, ActivityCommon.CreateValidFileName(vin));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private class EcuListAdapter : BaseAdapter<EcuInfo>
        {
            private readonly List<EcuInfo> _items;
            public List<EcuInfo> Items => _items;
            private readonly XmlToolActivity _context;
            private bool _ignoreCheckEvent;

            public EcuListAdapter(XmlToolActivity context)
            {
                _context = context;
                _items = new List<EcuInfo>();
            }

            public override long GetItemId(int position)
            {
                return position;
            }

            public override EcuInfo this[int position] => _items[position];

            public override int Count => _items.Count;

            public override View GetView(int position, View convertView, ViewGroup parent)
            {
                var item = _items[position];

                View view = convertView ?? _context.LayoutInflater.Inflate(Resource.Layout.ecu_select_list, null);
                CheckBox checkBoxSelect = view.FindViewById<CheckBox>(Resource.Id.checkBoxEcuSelect);
                _ignoreCheckEvent = true;
                checkBoxSelect.Checked = item.Selected;
                _ignoreCheckEvent = false;

                checkBoxSelect.Tag = new TagInfo(item);
                checkBoxSelect.CheckedChange -= OnCheckChanged;
                checkBoxSelect.CheckedChange += OnCheckChanged;

                TextView textEcuName = view.FindViewById<TextView>(Resource.Id.textEcuName);
                TextView textEcuDesc = view.FindViewById<TextView>(Resource.Id.textEcuDesc);

                StringBuilder stringBuilderName = new StringBuilder();
                stringBuilderName.Append(!string.IsNullOrEmpty(item.EcuName) ? item.EcuName : item.Name);
                if (!string.IsNullOrEmpty(item.Description))
                {
                    stringBuilderName.Append(": ");
                    stringBuilderName.Append(!string.IsNullOrEmpty(item.DescriptionTrans)
                        ? item.DescriptionTrans
                        : item.Description);
                }
                textEcuName.Text = stringBuilderName.ToString();

                StringBuilder stringBuilderInfo = new StringBuilder();
                stringBuilderInfo.Append(_context.GetString(Resource.String.xml_tool_info_sgbd));
                stringBuilderInfo.Append(": ");
                stringBuilderInfo.Append(item.Sgbd);
                if (!string.IsNullOrEmpty(item.Grp))
                {
                    stringBuilderInfo.Append(", ");
                    stringBuilderInfo.Append(_context.GetString(Resource.String.xml_tool_info_grp));
                    stringBuilderInfo.Append(": ");
                    stringBuilderInfo.Append(item.Grp);
                }
                if (!string.IsNullOrEmpty(item.Vin))
                {
                    stringBuilderInfo.Append(", ");
                    stringBuilderInfo.Append(_context.GetString(Resource.String.xml_tool_info_vin));
                    stringBuilderInfo.Append(": ");
                    stringBuilderInfo.Append(item.Vin);
                }
                textEcuDesc.Text = stringBuilderInfo.ToString();

                return view;
            }

            private void OnCheckChanged(object sender, CompoundButton.CheckedChangeEventArgs args)
            {
                if (!_ignoreCheckEvent)
                {
                    CheckBox checkBox = (CheckBox)sender;
                    TagInfo tagInfo = (TagInfo)checkBox.Tag;
                    if (tagInfo.Info.Selected != args.IsChecked)
                    {
                        tagInfo.Info.Selected = args.IsChecked;
                        _context.EcuCheckChanged(tagInfo.Info);
                        NotifyDataSetChanged();
                    }
                }
            }

            private class TagInfo : Java.Lang.Object
            {
                public TagInfo(EcuInfo info)
                {
                    Info = info;
                }

                public EcuInfo Info { get; }
            }
        }
    }
}
