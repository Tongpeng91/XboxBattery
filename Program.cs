using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using DW = System.Drawing;
using D2D = System.Drawing.Drawing2D;
using WF = System.Windows.Forms;

namespace XboxBattery
{
    internal static class XInputNative
    {
        public const byte BATTERY_DEVTYPE_GAMEPAD = 0x00;

        public const byte BATTERY_TYPE_DISCONNECTED = 0x00;
        public const byte BATTERY_TYPE_WIRED = 0x01;
        public const byte BATTERY_TYPE_ALKALINE = 0x02;
        public const byte BATTERY_TYPE_NIMH = 0x03;
        public const byte BATTERY_TYPE_UNKNOWN = 0xFF;

        public const byte BATTERY_LEVEL_EMPTY = 0x00;
        public const byte BATTERY_LEVEL_LOW = 0x01;
        public const byte BATTERY_LEVEL_MEDIUM = 0x02;
        public const byte BATTERY_LEVEL_FULL = 0x03;

        [StructLayout(LayoutKind.Sequential)]
        public struct BatteryInformation
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        public static extern uint GetBatteryInformation(int userIndex, byte devType, ref BatteryInformation info);
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            bool createdNew;
            Mutex mutex = new Mutex(true, "XboxBatteryWidget_SingleInstance_v1", out createdNew);
            if (!createdNew)
            {
                return;
            }

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            MainWindow win = new MainWindow();
            win.Show();

            app.Run();

            GC.KeepAlive(mutex);
        }
    }

    internal class BtDevice
    {
        public string Name;
        public ulong Address;
        public bool IsKeyboard;
        public int BatteryPct = -1; // -1 = 未知（设备未提供标准电量上报）
    }

    public class MainWindow : Window
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "XboxBatteryWidget";
        private const string SettingsKeyPath = @"Software\XboxBatteryWidget";
        private const double BaseWidth = 344;
        private const double BaseHeightXbox = 176;
        private const double BtRowHeight = 46;
        private const double BtSepHeight = 15;
        private const double MinScale = 0.6;
        private const double MaxScale = 2.0;

        private static readonly Brush BrLow = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        private static readonly Brush BrMid = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        private static readonly Brush BrHigh = new SolidColorBrush(Color.FromRgb(0x35, 0xC4, 0x6B));
        private static readonly Brush BrWired = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        private static readonly Brush BrOff = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44));
        private static readonly Brush BrTrack = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x31));
        private static readonly Brush BrTextMain = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));
        private static readonly Brush BrTextSub = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6));

        private Path _fg;
        private TextBlock _pctText;
        private TextBlock _statusText;
        private TextBlock _detailText;
        private Ellipse _dot;

        private DispatcherTimer _timer;
        private WF.NotifyIcon _tray;
        private WF.ToolStripMenuItem _autoItem;
        private WF.ToolStripMenuItem _topItem;
        private volatile bool _bluetoothPresent;
        private bool _exiting;
        private bool _lowAlarmActive;

        private Border _card;
        private DropShadowEffect _cardEffect;
        private Brush _cardNormalBg;
        private Brush _cardLowBg;
        private Brush _cardNormalBorder;
        private Brush _cardLowBorder;

        private DW.Icon _iconHigh;
        private DW.Icon _iconMid;
        private DW.Icon _iconLow;
        private DW.Icon _iconEmpty;
        private DW.Icon _iconWired;
        private DW.Icon _iconOff;

        // v2: 卡片缩放
        private double _scale = 1.0;
        private ScaleTransform _scaleTransform;
        private bool _resizing;
        private Point _resizeStart;
        private double _resizeStartWidth;
        private WF.ToolStripMenuItem _sizeSmallItem;
        private WF.ToolStripMenuItem _sizeMidItem;
        private WF.ToolStripMenuItem _sizeLargeItem;

        // v2: 蓝牙鼠标 / 键盘电量
        private StackPanel _btPanel;
        private Border _btSep;
        private List<BtDevice> _btDevices = new List<BtDevice>();
        private Dictionary<ulong, Tuple<int, DateTime>> _btBatteryCache = new Dictionary<ulong, Tuple<int, DateTime>>();
        private string _btSig = "";
        private string _lastXboxTray = "Xbox 手柄电量";
        private string _btTraySuffix = "";

        public MainWindow()
        {
            this.WindowStyle = WindowStyle.None;
            this.AllowsTransparency = true;
            this.Background = Brushes.Transparent;
            this.ResizeMode = ResizeMode.NoResize;
            this.ShowInTaskbar = false;
            this.Topmost = LoadTopmost();
            _scale = LoadScale();

            BuildUi();
            BuildTray();
            EnsureAutostartOnFirstRun();
            ApplySize();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(2500);
            _timer.Tick += OnTick;
            _timer.Start();
            OnTick(null, null);

            Thread btThread = new Thread(BtLoop);
            btThread.IsBackground = true;
            btThread.Start();

            Thread btDevThread = new Thread(BtDeviceLoop);
            btDevThread.IsBackground = true;
            btDevThread.Start();

            this.Closing += OnClosing;
        }

        // ------------------------------------------------------------ UI ---
        private void BuildUi()
        {
            Grid root = new Grid();
            root.Margin = new Thickness(14);
            _scaleTransform = new ScaleTransform(_scale, _scale);
            root.LayoutTransform = _scaleTransform;

            _card = new Border();
            Border card = _card;
            card.CornerRadius = new CornerRadius(18);
            card.BorderThickness = new Thickness(1);
            _cardNormalBorder = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            _cardLowBorder = new SolidColorBrush(Color.FromArgb(200, 255, 96, 96));
            card.BorderBrush = _cardNormalBorder;

            _cardNormalBg = MakeCardBg(Color.FromRgb(0x1E, 0x1E, 0x26), Color.FromRgb(0x2C, 0x2C, 0x36));
            _cardLowBg = MakeCardBg(Color.FromRgb(0x32, 0x10, 0x12), Color.FromRgb(0x4A, 0x18, 0x1C));
            card.Background = _cardNormalBg;

            _cardEffect = new DropShadowEffect
            {
                Color = Color.FromArgb(160, 0, 0, 0),
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.55
            };
            card.Effect = _cardEffect;

            card.Padding = new Thickness(16);
            root.Children.Add(card);

            Grid layout = new Grid();
            ColumnDefinition c0 = new ColumnDefinition();
            c0.Width = new GridLength(132);
            ColumnDefinition c1 = new ColumnDefinition();
            c1.Width = new GridLength(1, GridUnitType.Star);
            layout.ColumnDefinitions.Add(c0);
            layout.ColumnDefinitions.Add(c1);

            // gauge
            Grid gauge = new Grid();
            gauge.Width = 132;
            gauge.Height = 132;

            Canvas cv = new Canvas();
            cv.Width = 132;
            cv.Height = 132;

            Path track = new Path();
            track.Data = BuildArc(66, 66, 46, 225, 270);
            track.Stroke = BrTrack;
            track.StrokeThickness = 9;
            track.StrokeStartLineCap = PenLineCap.Round;
            track.StrokeEndLineCap = PenLineCap.Round;
            cv.Children.Add(track);

            _fg = new Path();
            _fg.Data = BuildArc(66, 66, 46, 225, 0);
            _fg.Stroke = BrHigh;
            _fg.StrokeThickness = 9;
            _fg.StrokeStartLineCap = PenLineCap.Round;
            _fg.StrokeEndLineCap = PenLineCap.Round;
            _fg.Opacity = 0;
            cv.Children.Add(_fg);

            gauge.Children.Add(cv);

            _pctText = new TextBlock();
            _pctText.FontSize = 30;
            _pctText.FontWeight = FontWeights.SemiBold;
            _pctText.Foreground = BrTextMain;
            _pctText.Text = "--";
            _pctText.HorizontalAlignment = HorizontalAlignment.Center;
            _pctText.VerticalAlignment = VerticalAlignment.Center;
            gauge.Children.Add(_pctText);

            Grid.SetColumn(gauge, 0);
            layout.Children.Add(gauge);

            // text column
            StackPanel right = new StackPanel();
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Margin = new Thickness(6, 0, 0, 0);

            _dot = new Ellipse();
            _dot.Width = 9;
            _dot.Height = 9;
            _dot.Fill = BrOff;

            TextBlock title = new TextBlock();
            title.Text = "Xbox 手柄";
            title.FontSize = 17;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = BrTextMain;

            StackPanel titleRow = new StackPanel();
            titleRow.Orientation = Orientation.Horizontal;
            titleRow.Children.Add(_dot);
            TextBlock dotPad = new TextBlock();
            dotPad.Width = 8;
            titleRow.Children.Add(dotPad);
            titleRow.Children.Add(title);
            right.Children.Add(titleRow);

            _statusText = new TextBlock();
            _statusText.FontSize = 12.5;
            _statusText.Foreground = BrTextSub;
            _statusText.Margin = new Thickness(0, 8, 0, 0);
            right.Children.Add(_statusText);

            _detailText = new TextBlock();
            _detailText.FontSize = 11;
            _detailText.Foreground = BrTextSub;
            _detailText.Margin = new Thickness(0, 4, 0, 0);
            _detailText.TextWrapping = TextWrapping.Wrap;
            right.Children.Add(_detailText);

            Grid.SetColumn(right, 1);
            layout.Children.Add(right);

            // v2: 卡片内容 = Xbox 行 + 分隔线 + 蓝牙设备行（右侧留 14px 给缩放角）
            StackPanel content = new StackPanel();
            content.Margin = new Thickness(0, 0, 14, 0);
            content.Children.Add(layout);

            _btSep = new Border();
            _btSep.Height = 1;
            _btSep.Background = BrTrack;
            _btSep.Margin = new Thickness(0, 10, 0, 4);
            _btSep.Visibility = Visibility.Collapsed;
            content.Children.Add(_btSep);

            _btPanel = new StackPanel();
            content.Children.Add(_btPanel);

            // v2: 覆盖层，放缩放角
            Grid overlay = new Grid();
            overlay.Children.Add(content);

            Border grip = new Border();
            grip.Width = 20;
            grip.Height = 20;
            grip.Background = Brushes.Transparent;
            grip.Cursor = Cursors.SizeNWSE;
            grip.HorizontalAlignment = HorizontalAlignment.Right;
            grip.VerticalAlignment = VerticalAlignment.Bottom;
            grip.ToolTip = "拖动调整卡片大小";
            TextBlock gripMark = new TextBlock();
            gripMark.Text = "⋰";
            gripMark.FontSize = 15;
            gripMark.Foreground = BrTextSub;
            gripMark.Opacity = 0.45;
            gripMark.HorizontalAlignment = HorizontalAlignment.Center;
            gripMark.VerticalAlignment = VerticalAlignment.Center;
            grip.Child = gripMark;
            grip.MouseLeftButtonDown += GripDown;
            grip.MouseMove += GripMove;
            grip.MouseLeftButtonUp += GripUp;
            overlay.Children.Add(grip);

            card.Child = overlay;

            this.Content = root;

            // drag anywhere
            root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    this.DragMove();
                }
            };
        }

        // ------------------------------------------------------- tray icon ---
        private void BuildTray()
        {
            _iconHigh = MakeIcon(DW.Color.FromArgb(255, 53, 196, 107), 1.0);
            _iconMid = MakeIcon(DW.Color.FromArgb(255, 245, 158, 11), 0.60);
            _iconLow = MakeIcon(DW.Color.FromArgb(255, 239, 68, 68), 0.25);
            _iconEmpty = MakeIcon(DW.Color.FromArgb(255, 239, 68, 68), 0.05);
            _iconWired = MakeIcon(DW.Color.FromArgb(255, 59, 130, 246), 1.0);
            _iconOff = MakeIcon(DW.Color.FromArgb(255, 120, 120, 130), 0.0);

            _tray = new WF.NotifyIcon();
            _tray.Icon = _iconOff;
            _tray.Text = "Xbox 手柄电量";
            _tray.Visible = true;

            WF.ContextMenuStrip menu = new WF.ContextMenuStrip();

            WF.ToolStripMenuItem show = new WF.ToolStripMenuItem("显示 / 隐藏窗口");
            show.Click += delegate { ToggleWindow(); };
            menu.Items.Add(show);

            _autoItem = new WF.ToolStripMenuItem("开机自启动");
            _autoItem.CheckOnClick = true;
            _autoItem.Checked = IsAutostartEnabled();
            _autoItem.CheckedChanged += delegate { SetAutostart(_autoItem.Checked); };
            menu.Items.Add(_autoItem);

            _topItem = new WF.ToolStripMenuItem("窗口置顶");
            _topItem.CheckOnClick = true;
            _topItem.Checked = this.Topmost;
            _topItem.CheckedChanged += delegate { this.Topmost = _topItem.Checked; };
            menu.Items.Add(_topItem);

            // v2: 卡片大小
            WF.ToolStripMenuItem sizeMenu = new WF.ToolStripMenuItem("卡片大小");
            _sizeSmallItem = new WF.ToolStripMenuItem("小");
            _sizeSmallItem.Click += delegate { SetScale(0.75, true); };
            _sizeMidItem = new WF.ToolStripMenuItem("中");
            _sizeMidItem.Click += delegate { SetScale(1.0, true); };
            _sizeLargeItem = new WF.ToolStripMenuItem("大");
            _sizeLargeItem.Click += delegate { SetScale(1.5, true); };
            sizeMenu.DropDownItems.Add(_sizeSmallItem);
            sizeMenu.DropDownItems.Add(_sizeMidItem);
            sizeMenu.DropDownItems.Add(_sizeLargeItem);
            menu.Items.Add(sizeMenu);
            UpdateSizeChecks();

            menu.Items.Add(new WF.ToolStripSeparator());

            WF.ToolStripMenuItem exit = new WF.ToolStripMenuItem("退出");
            exit.Click += delegate
            {
                _exiting = true;
                _timer.Stop();
                _tray.Visible = false;
                _tray.Dispose();
                Application.Current.Shutdown();
            };
            menu.Items.Add(exit);

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ToggleWindow(); };
        }

        // ------------------------------------------------------------ poll ---
        private void OnTick(object sender, EventArgs e)
        {
            bool found = false;
            byte type = 0;
            byte level = 0;
            for (int i = 0; i < 4; i++)
            {
                XInputNative.BatteryInformation bi = new XInputNative.BatteryInformation();
                uint hr = XInputNative.GetBatteryInformation(i, XInputNative.BATTERY_DEVTYPE_GAMEPAD, ref bi);
                if (hr == 0)
                {
                    found = true;
                    type = bi.BatteryType;
                    level = bi.BatteryLevel;
                    break;
                }
            }

            if (found)
            {
                ShowBattery(type, level);
            }
            else if (_bluetoothPresent)
            {
                ShowBluetooth();
            }
            else
            {
                ShowDisconnected();
            }
        }

        private void BtLoop()
        {
            while (true)
            {
                try
                {
                    _bluetoothPresent = DetectBluetoothXbox();
                }
                catch
                {
                    _bluetoothPresent = false;
                }
                Thread.Sleep(6000);
            }
        }

        private static bool DetectBluetoothXbox()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'HID%VID_045E%'"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string id = (mo["PNPDeviceID"] == null) ? "" : mo["PNPDeviceID"].ToString();
                        string upper = id.ToUpperInvariant();
                        if (upper.IndexOf("PID_02E6") >= 0) continue; // 无线适配器本体
                        if (upper.IndexOf("PID_02D1") >= 0) continue;
                        if (upper.IndexOf("PID_02EA") >= 0) continue;
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        // ------------------------------------------- 蓝牙鼠标 / 键盘电量 ---
        private void BtDeviceLoop()
        {
            Thread.Sleep(3000); // 等主界面起来再开始
            while (true)
            {
                try
                {
                    RefreshBtDevicesAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // 忽略单次失败，下个周期重试
                }
                Thread.Sleep(15000);
            }
        }

        // WinRT 异步操作加超时：直接 await（.NET 5+ 内置支持），超时则放弃
        private static async Task<T> WithTimeout<T>(Windows.Foundation.IAsyncOperation<T> op, int milliseconds)
        {
            Task<T> task = Task.Run(async () => await op);
            Task done = await Task.WhenAny(task, Task.Delay(milliseconds));
            if (done != task) throw new TimeoutException();
            return await task;
        }

        private async Task RefreshBtDevicesAsync()
        {
            List<BtDevice> list = new List<BtDevice>();
            try
            {
                var infos = await WithTimeout(DeviceInformation.FindAllAsync(
                    BluetoothDevice.GetDeviceSelectorFromPairingState(true)), 15000);
                foreach (DeviceInformation info in infos)
                {
                    BluetoothDevice dev = null;
                    try
                    {
                        dev = await WithTimeout(BluetoothDevice.FromIdAsync(info.Id), 5000);
                    }
                    catch
                    {
                        continue;
                    }
                    if (dev == null) continue;
                    using (dev)
                    {
                        int major = (int)dev.ClassOfDevice.MajorClass;
                        if (major != 5) continue; // Peripheral 外设
                        int minor = (int)dev.ClassOfDevice.MinorClass;
                        bool isKb = (minor & 0x40) != 0;    // 键盘
                        bool isMouse = (minor & 0x80) != 0; // 鼠标 / 指向设备
                        if (!isKb && !isMouse) continue;
                        if (dev.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;
                        string name = string.IsNullOrEmpty(dev.Name) ? info.Name : dev.Name;
                        if (string.IsNullOrEmpty(name)) name = isKb ? "蓝牙键盘" : "蓝牙鼠标";
                        ulong addr = dev.BluetoothAddress;
                        int pct = await GetBtBatteryAsync(addr);
                        list.Add(new BtDevice { Name = name, Address = addr, IsKeyboard = isKb, BatteryPct = pct });
                    }
                }
            }
            catch
            {
                return;
            }

            // 清理已断开设备的电量缓存
            var alive = new HashSet<ulong>(list.Select(d => d.Address));
            var stale = _btBatteryCache.Keys.Where(a => !alive.Contains(a)).ToList();
            foreach (ulong a in stale) _btBatteryCache.Remove(a);

            _ = Dispatcher.BeginInvoke(new Action(() => ApplyBtDevices(list)));
        }

        private async Task<int> GetBtBatteryAsync(ulong address)
        {
            Tuple<int, DateTime> cached;
            if (_btBatteryCache.TryGetValue(address, out cached))
            {
                if ((DateTime.UtcNow - cached.Item2).TotalSeconds < 60) return cached.Item1;
            }
            int pct = await ReadBtBatteryGattAsync(address);
            _btBatteryCache[address] = Tuple.Create(pct, DateTime.UtcNow);
            return pct;
        }

        private static async Task<int> ReadBtBatteryGattAsync(ulong address)
        {
            try
            {
                BluetoothLEDevice le = await WithTimeout(BluetoothLEDevice.FromBluetoothAddressAsync(address), 5000);
                if (le == null) return -1;
                using (le)
                {
                    var svcRes = await WithTimeout(le.GetGattServicesForUuidAsync(GattServiceUuids.Battery), 5000);
                    if (svcRes.Status != GattCommunicationStatus.Success || svcRes.Services.Count == 0) return -1;
                    using (GattDeviceService svc = svcRes.Services[0])
                    {
                        var chRes = await WithTimeout(svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel), 5000);
                        if (chRes.Status != GattCommunicationStatus.Success || chRes.Characteristics.Count == 0) return -1;
                        GattCharacteristic ch = chRes.Characteristics[0];
                        var read = await WithTimeout(ch.ReadValueAsync(), 5000);
                        if (read.Status != GattCommunicationStatus.Success) return -1;
                        if (read.Value == null || read.Value.Length < 1) return -1;
                        DataReader reader = DataReader.FromBuffer(read.Value);
                        if (reader.UnconsumedBufferLength < 1) return -1;
                        return reader.ReadByte();
                    }
                }
            }
            catch
            {
                return -1;
            }
        }

        private void ApplyBtDevices(List<BtDevice> list)
        {
            string sig = string.Join("|", list.Select(d => d.Address.ToString() + ":" + d.BatteryPct));
            if (sig == _btSig && list.Count == _btDevices.Count) return;
            _btSig = sig;
            _btDevices = list;
            _btPanel.Children.Clear();
            if (list.Count == 0)
            {
                _btSep.Visibility = Visibility.Collapsed;
            }
            else
            {
                _btSep.Visibility = Visibility.Visible;
                foreach (BtDevice d in list)
                {
                    _btPanel.Children.Add(BuildBtRow(d));
                }
            }
            UpdateBtTraySuffix();
            UpdateTrayText();
            ApplySize();
        }

        private Grid BuildBtRow(BtDevice d)
        {
            Grid row = new Grid();
            row.Height = 40;
            row.Margin = new Thickness(0, 6, 0, 0);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            TextBlock type = new TextBlock();
            type.Text = d.IsKeyboard ? "键盘" : "鼠标";
            type.FontSize = 11;
            type.Foreground = BrTextSub;
            type.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(type, 0);
            row.Children.Add(type);

            TextBlock name = new TextBlock();
            name.Text = d.Name;
            name.FontSize = 13;
            name.Foreground = BrTextMain;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            Border track = new Border();
            track.Width = 88;
            track.Height = 8;
            track.CornerRadius = new CornerRadius(4);
            track.Background = BrTrack;
            track.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(track, 2);
            row.Children.Add(track);

            Border fill = new Border();
            fill.Height = 8;
            fill.CornerRadius = new CornerRadius(4);
            fill.Background = BtBrushFor(d.BatteryPct);
            fill.HorizontalAlignment = HorizontalAlignment.Left;
            fill.VerticalAlignment = VerticalAlignment.Center;
            fill.Width = d.BatteryPct < 0 ? 0 : Math.Max(4, 88.0 * d.BatteryPct / 100.0);
            Grid.SetColumn(fill, 2);
            row.Children.Add(fill);

            TextBlock pct = new TextBlock();
            pct.Text = d.BatteryPct < 0 ? "未知" : (d.BatteryPct + "%");
            pct.FontSize = 13;
            pct.Foreground = BrTextSub;
            pct.VerticalAlignment = VerticalAlignment.Center;
            pct.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(pct, 3);
            row.Children.Add(pct);

            row.ToolTip = d.Name + (d.IsKeyboard ? "（键盘）" : "（鼠标）") + " · " +
                (d.BatteryPct < 0 ? "电量未知（设备未提供标准上报）" : ("剩余电量 " + d.BatteryPct + "%"));
            return row;
        }

        private static Brush BtBrushFor(int pct)
        {
            if (pct < 0) return BrOff;
            if (pct < 20) return BrLow;
            if (pct < 50) return BrMid;
            return BrHigh;
        }

        private void UpdateBtTraySuffix()
        {
            if (_btDevices.Count == 0)
            {
                _btTraySuffix = "";
                return;
            }
            List<string> parts = new List<string>();
            foreach (BtDevice d in _btDevices)
            {
                string n = d.Name;
                if (n.Length > 8) n = n.Substring(0, 8);
                parts.Add(n + " " + (d.BatteryPct < 0 ? "未知" : (d.BatteryPct + "%")));
            }
            _btTraySuffix = " | " + string.Join(" · ", parts);
        }

        private void UpdateTrayText()
        {
            if (_tray == null) return;
            string t = _lastXboxTray + _btTraySuffix;
            if (t.Length > 120) t = t.Substring(0, 120);
            _tray.Text = t;
        }

        // ------------------------------------------------------- state views ---
        private void ShowBattery(byte type, byte level)
        {
            int pct;
            Brush color;
            string status;
            string detail;
            DW.Icon icon;

            if (type == XInputNative.BATTERY_TYPE_WIRED)
            {
                pct = 100;
                color = BrWired;
                status = "已连接 · 有线供电";
                detail = "USB 连接，持续供电";
                icon = _iconWired;
            }
            else
            {
                switch (level)
                {
                    case XInputNative.BATTERY_LEVEL_EMPTY: pct = 5; color = BrLow; status = "已连接 · 无线适配器"; detail = "电量即将耗尽，请更换电池"; icon = _iconEmpty; break;
                    case XInputNative.BATTERY_LEVEL_LOW: pct = 25; color = BrLow; status = "已连接 · 无线适配器"; detail = "电量偏低"; icon = _iconLow; break;
                    case XInputNative.BATTERY_LEVEL_MEDIUM: pct = 60; color = BrMid; status = "已连接 · 无线适配器"; detail = "电量中等"; icon = _iconMid; break;
                    default: pct = 100; color = BrHigh; status = "已连接 · 无线适配器"; detail = "电量充足"; icon = _iconHigh; break;
                }
            }

            bool low = (type != XInputNative.BATTERY_TYPE_WIRED) && (level <= XInputNative.BATTERY_LEVEL_LOW);
            if (low && !_lowAlarmActive)
            {
                _lowAlarmActive = true;
                PlayDoubleBeep();
            }
            if (!low)
            {
                _lowAlarmActive = false;
            }
            SetLowVisual(low);

            if (type == XInputNative.BATTERY_TYPE_WIRED)
            {
                _lastXboxTray = "Xbox 手柄 · 有线供电";
            }
            else
            {
                _lastXboxTray = "Xbox 手柄 · 剩余电量 " + pct + "%";
            }
            UpdateTrayText();

            SetView(pct, color, status, detail, icon, true);
        }

        private void ShowBluetooth()
        {
            _lowAlarmActive = false;
            SetLowVisual(false);
            _lastXboxTray = "Xbox 手柄 · 蓝牙连接（无法读取电量）";
            UpdateTrayText();
            SetView(-1, BrOff, "已连接（蓝牙）", "蓝牙模式下系统读不到电量，改用 USB 或官方无线适配器可显示真实电量", _iconOff, false);
        }

        private void ShowDisconnected()
        {
            _lowAlarmActive = false;
            SetLowVisual(false);
            _lastXboxTray = "Xbox 手柄 · 未连接";
            UpdateTrayText();
            SetView(-1, BrOff, "未连接手柄", "请连接 Xbox 手柄（USB / 官方无线适配器 / 蓝牙）", _iconOff, false);
        }

        private void SetView(int pct, Brush color, string status, string detail, DW.Icon icon, bool hasLevel)
        {
            _pctText.Text = pct < 0 ? "--" : (pct.ToString() + "%");
            _statusText.Text = status;
            _detailText.Text = detail;
            _dot.Fill = color;
            _tray.Icon = icon;

            if (hasLevel && pct >= 0)
            {
                double frac = pct / 100.0;
                if (frac < 0.02) frac = 0.02;
                _fg.Stroke = color;
                _fg.Data = BuildArc(66, 66, 46, 225, 270 * frac);
                _fg.Opacity = 1;
                _pctText.Foreground = BrTextMain;
            }
            else
            {
                _fg.Opacity = 0;
                _pctText.Foreground = BrTextSub;
            }
        }

        private void SetLowVisual(bool low)
        {
            if (_card == null) return;
            if (low)
            {
                _card.Background = _cardLowBg;
                _card.BorderBrush = _cardLowBorder;
                _cardEffect.Color = Color.FromArgb(210, 255, 60, 60);
            }
            else
            {
                _card.Background = _cardNormalBg;
                _card.BorderBrush = _cardNormalBorder;
                _cardEffect.Color = Color.FromArgb(160, 0, 0, 0);
            }
        }

        private void PlayDoubleBeep()
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    System.Media.SystemSounds.Exclamation.Play();
                    Thread.Sleep(280);
                    System.Media.SystemSounds.Exclamation.Play();
                }
                catch
                {
                    try
                    {
                        System.Console.Beep(1100, 220);
                        Thread.Sleep(280);
                        System.Console.Beep(1100, 220);
                    }
                    catch
                    {
                    }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ------------------------------------------------------- 卡片缩放 ---
        private void ApplySize()
        {
            double baseH = BaseHeightXbox;
            if (_btDevices != null && _btDevices.Count > 0)
            {
                baseH += BtSepHeight + _btDevices.Count * BtRowHeight;
            }
            this.Width = BaseWidth * _scale;
            this.Height = baseH * _scale;
        }

        private void SetScale(double s, bool save)
        {
            if (s < MinScale) s = MinScale;
            if (s > MaxScale) s = MaxScale;
            _scale = s;
            if (_scaleTransform != null)
            {
                _scaleTransform.ScaleX = s;
                _scaleTransform.ScaleY = s;
            }
            ApplySize();
            UpdateSizeChecks();
            if (save) SaveScale();
        }

        private void UpdateSizeChecks()
        {
            if (_sizeSmallItem == null) return;
            double dS = Math.Abs(_scale - 0.75);
            double dM = Math.Abs(_scale - 1.0);
            double dL = Math.Abs(_scale - 1.5);
            _sizeSmallItem.Checked = dS <= dM && dS <= dL;
            _sizeMidItem.Checked = dM < dS && dM <= dL;
            _sizeLargeItem.Checked = dL < dS && dL < dM;
        }

        private static double LoadScale()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKeyPath))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("Scale");
                        if (v != null)
                        {
                            double s;
                            if (double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out s))
                            {
                                if (s < MinScale) s = MinScale;
                                if (s > MaxScale) s = MaxScale;
                                return s;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return 1.0;
        }

        private void SaveScale()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
                {
                    if (k != null) k.SetValue("Scale", _scale.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch
            {
            }
        }

        private void GripDown(object sender, MouseButtonEventArgs e)
        {
            _resizing = true;
            _resizeStart = e.GetPosition(this);
            _resizeStartWidth = this.Width;
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void GripMove(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;
            Point p = e.GetPosition(this);
            double newWidth = _resizeStartWidth + (p.X - _resizeStart.X);
            SetScale(newWidth / BaseWidth, false);
        }

        private void GripUp(object sender, MouseButtonEventArgs e)
        {
            if (!_resizing) return;
            _resizing = false;
            ((UIElement)sender).ReleaseMouseCapture();
            SaveScale();
        }

        // ---------------------------------------------------------- helpers ---
        private void ToggleWindow()
        {
            if (this.IsVisible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
                this.Activate();
            }
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_exiting)
            {
                e.Cancel = true;
                this.Hide();
            }
        }

        private static LinearGradientBrush MakeCardBg(Color c0, Color c1)
        {
            LinearGradientBrush bg = new LinearGradientBrush();
            bg.StartPoint = new Point(0, 0);
            bg.EndPoint = new Point(1, 1);
            bg.GradientStops.Add(new GradientStop(c0, 0.0));
            bg.GradientStops.Add(new GradientStop(c1, 1.0));
            return bg;
        }

        private static Geometry BuildArc(double cx, double cy, double r, double startDeg, double sweepDeg)
        {
            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                Point first = Polar(cx, cy, r, startDeg);
                ctx.BeginFigure(first, false, false);
                int n = 120;
                for (int i = 1; i <= n; i++)
                {
                    double ang = startDeg + sweepDeg * i / n;
                    ctx.LineTo(Polar(cx, cy, r, ang), true, false);
                }
            }
            g.Freeze();
            return g;
        }

        private static Point Polar(double cx, double cy, double r, double deg)
        {
            double a = (deg - 90.0) * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }

        // ---------------------------------------------------------- autostart ---
        private static bool IsAutostartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    return k != null && k.GetValue(AppName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ExePath()
        {
            // .NET 8 下 Assembly.Location 返回的是 .dll 路径，开机自启要用 .exe 路径
            return Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
        }

        private void SetAutostart(bool enable)
        {
            try
            {
                string exe = ExePath();
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return;
                    if (enable)
                    {
                        k.SetValue(AppName, "\"" + exe + "\"");
                    }
                    else
                    {
                        k.DeleteValue(AppName, false);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        private void EnsureAutostartOnFirstRun()
        {
            try
            {
                string exe = ExePath();
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return;
                    object v = k.GetValue(AppName);
                    if (v == null)
                    {
                        k.SetValue(AppName, "\"" + exe + "\"");
                    }
                    else if (!string.Equals(v.ToString(), "\"" + exe + "\"", StringComparison.OrdinalIgnoreCase))
                    {
                        // 旧版残留的路径，更新为当前 exe
                        k.SetValue(AppName, "\"" + exe + "\"");
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        private static bool LoadTopmost()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\XboxBatteryWidget"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("Topmost");
                        if (v != null)
                        {
                            return v.ToString() == "1";
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        // ------------------------------------------------------- tray icon draw ---
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private static DW.Icon MakeIcon(DW.Color fill, double frac)
        {
            DW.Bitmap bmp = new DW.Bitmap(16, 16);
            using (DW.Graphics g = DW.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = D2D.SmoothingMode.AntiAlias;
                g.Clear(DW.Color.Transparent);
                using (DW.Pen p = new DW.Pen(DW.Color.White, 1.1f))
                {
                    g.DrawRectangle(p, 1.5f, 4.5f, 10.5f, 7.5f);
                    g.FillRectangle(DW.Brushes.White, 12.0f, 6.5f, 2.0f, 3.5f);
                }
                using (DW.SolidBrush b = new DW.SolidBrush(fill))
                {
                    double w = 9.0 * frac;
                    if (w > 0.3)
                    {
                        g.FillRectangle(b, 2.7f, 5.7f, (float)w, 5.1f);
                    }
                }
            }

            IntPtr h = bmp.GetHicon();
            DW.Icon clone;
            using (DW.Icon tmp = DW.Icon.FromHandle(h))
            {
                clone = (DW.Icon)tmp.Clone();
            }
            DestroyIcon(h);
            bmp.Dispose();
            return clone;
        }
    }
}
