using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ZKSWorkerGUI
{
    public partial class App : Form
    {
        #region 字段和属性
        private NotifyIcon? notifyIcon;
        private Process? _runningProcess; // 当前运行的进程
        private ConfigData? configData; // 配置数据
        private bool _dirty = false; // 是否有未保存的修改
        private bool _loading = false; // 是否正在加载配置，防止触发事件
        private ComboBoxItem? previousItem;

        // 托盘菜单项引用，用于状态同步
        private ToolStripMenuItem? _trayMenuServerGroup;
        private ToolStripMenuItem? _trayMenuProxyItem;
        private ToolStripMenuItem? _trayMenuAutoStartItem;
        private ToolStripMenuItem? _trayMenuStartItem;
        private ToolStripMenuItem? _trayMenuStopItem;

        #endregion

        #region 窗体初始化和事件处理
        public App()
        {
            InitializeComponent();
            this.StartPosition = FormStartPosition.CenterScreen;
            InitTray();

            textBoxToken.EnablePasswordToggle();
            textBoxServer.EnablePasswordToggle();
            textBoxIp.EnablePasswordToggle();
            textBoxFallback.EnablePasswordToggle();

            this.FormClosing += Form1_FormClosing;
            this.Resize += Form1_Resize;

            InitializeAutoStartHideCheckBox();
            LoadConfig();
            BindDirtyEvents();
            FillLastState();

            // 初始化托盘菜单状态同步
            SyncTrayMenuInitialState();
        }

        // 加载图标
        private Icon? LoadIcon()
        {
            try
            {
                var assembly = typeof(App).Assembly;
                var resourceNames = assembly.GetManifestResourceNames();

                string[] possibleNames =
                {
                    "ZKSWorkerGUI.app.ico",
                    "ZKSWorkerGUI.logo.ico",
                };

                foreach (string name in possibleNames)
                {
                    if (resourceNames.Contains(name))
                    {
                        var stream = assembly.GetManifestResourceStream(name);
                        if (stream != null)
                        {
                            return new Icon(stream);
                        }
                    }
                }
            }
            catch
            {
                // 如果加载嵌入图标失败，尝试从文件加载
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(icoPath))
                {
                    return new Icon(icoPath);
                }
            }
            return null;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            this.CenterToScreen();

            // 设置窗体图标
            var icon = LoadIcon();
            if (icon != null)
            {
                this.Icon = icon;
            }
        }

        // 关闭窗体最小化到托盘
        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
		{
			if (e.CloseReason == CloseReason.UserClosing)
			{
				// 1. 检查未保存更改
				if (_dirty)
				{
					var result = MessageBox.Show(
						"有未保存的更改，确定要退出吗？",
						"提示",
						MessageBoxButtons.YesNo,
						MessageBoxIcon.Warning
					);

					if (result == DialogResult.No)
					{
						e.Cancel = true;
						return;
					}
				}

				// 2. 在隐藏父窗体前，关闭所有属于“我”的子窗体
				var ownedForms = this.OwnedForms.ToList(); // 获取当前窗体拥有的所有子窗体快照
				foreach (var subForm in ownedForms)
				{
					subForm.Close();
				}

				// 3. 执行隐藏逻辑
				e.Cancel = true;
				this.Hide();
				notifyIcon?.ShowBalloonTip(1000, "提示", "程序已最小化到托盘", ToolTipIcon.Info);
			}
		}

        // 调整大小，正常最小化到任务栏
        private void Form1_Resize(object? sender, EventArgs e)
        {
            // 隐藏窗体
            // if (this.WindowState == FormWindowState.Minimized)
            // {
            //     this.Hide();
            // }
        }
        #endregion

        #region UI组件初始化

        // 初始化隐藏启动CheckBox
        private void InitializeAutoStartHideCheckBox()
        {
            // 初始状态为未勾选，等待开机启动CheckBox勾选
            checkBoxAutoStartHide.Checked = false;
            checkBoxAutoStartHide.Enabled = false;
        }

        private void BindDirtyEvents()
        {
            textBoxServer.TextChanged += AnyTextBox_TextChanged;
            textBoxToken.TextChanged += AnyTextBox_TextChanged;
            textBoxListen.TextChanged += AnyTextBox_TextChanged;
            textBoxIp.TextChanged += AnyTextBox_TextChanged;
            textBoxFallback.TextChanged += AnyTextBox_TextChanged;
        }
        #endregion

        #region 日志和进程管理
        private void AppendLog(string text)
        {
            if (textBoxLog.InvokeRequired)
            {
                textBoxLog.BeginInvoke(new Action<string>(AppendLog), text);
                return;
            }

            // 检查用户是否正在查看历史日志（不在底部）
            bool isAtBottom = textBoxLog.SelectionStart >= textBoxLog.TextLength - 100;

            // 限制日志长度，防止内存爆满
            const int maxLogLines = 1000;
            const int maxLogLength = 50000;

            // 添加新日志
            textBoxLog.AppendText(text + Environment.NewLine);

            // 检查并限制日志长度
            if (textBoxLog.Lines.Length > maxLogLines || textBoxLog.TextLength > maxLogLength)
            {
                // 保留最后的一半日志
                var lines = textBoxLog.Lines;
                var keepLines = Math.Min(maxLogLines / 2, lines.Length / 2);
                var startIndex = lines.Length - keepLines;

                if (startIndex > 0)
                {
                    var newLines = lines.Skip(startIndex).ToArray();
                    textBoxLog.Lines = newLines;

                    // 添加截断提示
                    textBoxLog.AppendText(
                        $"[日志已截断，保留最后{keepLines}行]{Environment.NewLine}"
                    );
                }
            }

            // 只有在用户原本在底部时才自动滚动
            if (isAtBottom)
            {
                textBoxLog.SelectionStart = textBoxLog.TextLength;
                textBoxLog.ScrollToCaret();
            }
        }

        // 杀掉进程
        private void KillRunningProcess()
        {
            if (_runningProcess == null)
                return;

            try
            {
                // 检查进程是否仍在运行
                if (!_runningProcess.HasExited)
                {
                    // 对于控制台程序，CloseMainWindow() 无效，直接尝试优雅关闭
                    try
                    {
                        // 先尝试发送 Ctrl+C 信号进行优雅关闭
                        _runningProcess.StandardInput?.WriteLine("exit");

                        // 等待 2 秒让程序优雅退出
                        if (!_runningProcess.WaitForExit(2000))
                        {
                            _runningProcess.Kill(true); // true：连子进程一起杀
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // 如果 StandardInput 不可用，直接强制关闭
                        _runningProcess.Kill(true);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"停止进程时发生错误: {ex.Message}");
            }
            finally
            {
                _runningProcess?.Dispose();
                _runningProcess = null;
            }
        }
        #endregion

        #region 托盘菜单管理
        private void InitTray()
        {
            var tray = new NotifyIcon();

            // 设置托盘图标
            var icon = LoadIcon();
            if (icon != null)
            {
                tray.Icon = icon;
            }

            tray.Text = "ZKS Workers Client";
            tray.Visible = true;

            // 双击托盘图标显示/隐藏窗体
            tray.DoubleClick += (_, __) =>
            {
                ToggleWindowVisibility();
            };

            var menu = new ContextMenuStrip();

            // 设置菜单字体大小，例如 11pt，默认是 9pt 左右
            menu.Font = new Font("Microsoft YaHei", 11, FontStyle.Regular);

            // 1. 显示/隐藏菜单项（最开头）
            var showHideMenuItem = new ToolStripMenuItem("显示/隐藏");
            showHideMenuItem.Click += (_, __) =>
            {
                ToggleWindowVisibility();
            };
            menu.Items.Add(showHideMenuItem);
            menu.Items.Add(new ToolStripSeparator());

            // 2. 启动控制菜单项（顶层）
            var startMenuItem = new ToolStripMenuItem("启动服务");
            startMenuItem.Enabled = buttonStart.Enabled;
            startMenuItem.Click += (_, __) =>
            {
                if (buttonStart.Enabled)
                    ButtonStart_Click(this, EventArgs.Empty);
            };
            menu.Items.Add(startMenuItem);

            var stopMenuItem = new ToolStripMenuItem("停止服务");
            stopMenuItem.Enabled = buttonStop.Enabled;
            stopMenuItem.Click += (_, __) =>
            {
                if (buttonStop.Enabled)
                    ButtonStop_Click(this, EventArgs.Empty);
            };
            menu.Items.Add(stopMenuItem);
            menu.Items.Add(new ToolStripSeparator());

            // 3. 系统设置菜单项（顶层，带状态图标）
            var proxyMenuItem = new ToolStripMenuItem("开启系统代理");
            proxyMenuItem.Checked = checkBoxSystemProxy.Checked;
            proxyMenuItem.Click += (_, __) =>
            {
                checkBoxSystemProxy.Checked = !checkBoxSystemProxy.Checked;
                proxyMenuItem.Checked = checkBoxSystemProxy.Checked;
                SetSystemProxy(checkBoxSystemProxy.Checked);
            };
            menu.Items.Add(proxyMenuItem);

            var autoStartMenuItem = new ToolStripMenuItem("开机启动");
            autoStartMenuItem.Checked = checkBoxAutoStart.Checked;
            autoStartMenuItem.Click += (_, __) =>
            {
                checkBoxAutoStart.Checked = !checkBoxAutoStart.Checked;
                autoStartMenuItem.Checked = checkBoxAutoStart.Checked;
                SetAutoStart(checkBoxAutoStart.Checked);
            };
            menu.Items.Add(autoStartMenuItem);
            menu.Items.Add(new ToolStripSeparator());

            // 4. 服务器选择分组
            var serverGroup = new ToolStripMenuItem("服务器选择");
            // 服务器选择主菜单项始终保持可用，子菜单项根据运行状态控制
            menu.Items.Add(serverGroup);

            // 添加服务器选择菜单项
            RefreshTrayServerMenu(serverGroup);

            // 5. 分隔线
            menu.Items.Add(new ToolStripSeparator());

            // 6. 退出菜单项
            var exitMenuItem = new ToolStripMenuItem("退出");
            exitMenuItem.Click += (_, __) =>
            {
                KillRunningProcess();
                tray.Visible = false; // 先隐藏托盘
                Application.Exit();
            };
            menu.Items.Add(exitMenuItem);

            // 保存托盘菜单项引用以便更新
            _trayMenuServerGroup = serverGroup;
            _trayMenuProxyItem = proxyMenuItem;
            _trayMenuAutoStartItem = autoStartMenuItem;
            _trayMenuStartItem = startMenuItem;
            _trayMenuStopItem = stopMenuItem;

            tray.ContextMenuStrip = menu;
            notifyIcon = tray; // 保存引用
        }

        // 切换窗口显示/隐藏状态
        private void ToggleWindowVisibility()
        {
            if (this.Visible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.BringToFront();
            }
        }

        // 同步托盘菜单初始状态
        private void SyncTrayMenuInitialState()
        {
            // 同步系统代理状态
            if (_trayMenuProxyItem != null)
                _trayMenuProxyItem.Checked = checkBoxSystemProxy.Checked;

            // 同步开机启动状态
            if (_trayMenuAutoStartItem != null)
                _trayMenuAutoStartItem.Checked = checkBoxAutoStart.Checked;

            // 同步启动/停止状态
            if (_trayMenuStartItem != null)
                _trayMenuStartItem.Enabled = buttonStart.Enabled;
            if (_trayMenuStopItem != null)
                _trayMenuStopItem.Enabled = buttonStop.Enabled;

            // 同步服务器选择状态
            if (_trayMenuServerGroup != null)
            {
                RefreshTrayServerMenu(_trayMenuServerGroup);
                // 服务器选择主菜单项始终保持可用，不根据运行状态禁用
                _trayMenuServerGroup.Enabled = true;
            }
        }

        // 刷新托盘服务器菜单
        private void RefreshTrayServerMenu(ToolStripMenuItem serverGroup)
        {
            serverGroup.DropDownItems.Clear();

            if (configData?.servers == null)
                return;

            // 检查运行状态
            bool isRunning = _runningProcess != null && !_runningProcess.HasExited;

            foreach (var server in configData.servers)
            {
                var serverMenuItem = new ToolStripMenuItem(server.name);
                serverMenuItem.Checked = server.id == configData.current_server_id;

                serverMenuItem.Enabled = !isRunning; // 运行时禁用选择

                serverMenuItem.Click += (_, __) =>
                {
                    // 在点击时重新检查运行状态
                    bool currentlyRunning = _runningProcess != null && !_runningProcess.HasExited;
                    if (currentlyRunning)
                        return; // 运行时不允许切换

                    // 更新UI界面
                    var item = comboBoxServics
                        .Items.Cast<ComboBoxItem>()
                        .FirstOrDefault(x => x.Id == server.id);
                    if (item != null)
                    {
                        _loading = true;
                        comboBoxServics.SelectedItem = item;
                        configData!.current_server_id = item.Id;
                        FillServerDetails(item.ServerInfo);
                        previousItem = item;
                        _loading = false;

                        // 静默保存配置
                        SaveConfigToFile(false);
                    }

                    // 更新托盘菜单状态
                    RefreshTrayServerMenu(serverGroup);
                };
                serverGroup.DropDownItems.Add(serverMenuItem);
            }
        }
        #endregion

        #region 配置文件管理
        // 读取配置
        private void LoadConfig()
        {
            // 定义配置文件查找路径数组
            string[] configPaths =
            {
                Path.Combine(Application.StartupPath, "config.json"), // 主程序的根目录
                //Path.Combine( // AppData目录
                //    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                //    "ECHWorkersClient",
                //    "config.json"
                //),
            };

            // 按优先级查找配置文件
            string configPath = string.Empty;
            foreach (string path in configPaths)
            {
                if (File.Exists(path))
                {
                    configPath = path;
                    break;
                }
            }

            // 如果没有找到任何配置文件，创建默认配置
            if (string.IsNullOrEmpty(configPath))
            {
                configPath = configPaths[0];
                CreateDefaultConfig(configPath);
            }

            var json = File.ReadAllText(configPath);
            configData = JsonSerializer.Deserialize<ConfigData>(json);

            comboBoxServics.Items.Clear();

            if (configData?.servers == null)
            {
                MessageBox.Show("servers 配置为空");
                return;
            }

            foreach (var s in configData.servers)
            {
                comboBoxServics.Items.Add(
                    new ComboBoxItem
                    {
                        Id = s.id,
                        Name = s.name,
                        ServerInfo = s,
                    }
                );
            }

            var currentItem = comboBoxServics
                .Items.Cast<ComboBoxItem>()
                .FirstOrDefault(x => x.Id == configData.current_server_id);

            if (currentItem != null)
            {
                comboBoxServics.SelectedItem = currentItem;
                FillServerDetails(currentItem.ServerInfo);
                previousItem = currentItem; // 初始化 previousItem
            }

            comboBoxServics.SelectedIndexChanged += ComboBox1_SelectedIndexChanged;
        }

        // 创建默认配置
        private void CreateDefaultConfig(string configPath)
        {
            // 确保目录存在
            string directory = Path.GetDirectoryName(configPath)!;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 生成UUID和随机端口
            string serverId = Guid.NewGuid().ToString();
            int randomPort = GenerateRandomPort();

            // 创建默认配置数据
            configData = new ConfigData
            {
                current_server_id = serverId,
                servers = new[]
                {
                    new Server
                    {
                        id = serverId,
                        name = "默认服务器",
                        server = "worker.username.workers.dev/session",
                        listen = $"127.0.0.1:{randomPort}",
                        token = "",
                        ip = "r2.dev",
                        fallback = "",
                    },
                },
                last_state = new LastState
                {
                    system_proxy_enabled = false,
                    auto_start_checked = false,
                    was_running = false,
                },
            };

            // 保存默认配置到文件
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            string json = JsonSerializer.Serialize(configData, options);
            File.WriteAllText(configPath, json);

            MessageBox.Show("已创建默认配置文件");
        }

        // 生成随机端口，避开5位数字端口（10000-65535）
        private int GenerateRandomPort()
        {
            Random random = new Random();
            int port;

            // 尝试生成4位数字端口（1024-9999）
            int attempts = 0;
            do
            {
                port = random.Next(1024, 10000); // 1024-9999范围
                attempts++;

                // 检查端口是否可用
                if (IsPortAvailable(port))
                {
                    return port;
                }
            } while (attempts < 50); // 最多尝试50次

            // 如果4位端口都用完了，使用系统分配
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            return port;
        }

        // 检查端口是否可用
        private bool IsPortAvailable(int port)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void SaveConfigToFile()
        {
            SaveConfigToFile(true);
        }

        private void SaveConfigToFile(bool showMessage)
        {
            try
            {
                // 定义配置文件查找路径数组
                string[] configPaths =
                {
                    Path.Combine(Application.StartupPath, "config.json"), // 根目录
                    //Path.Combine( // AppData目录
                    //    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    //    "ECHWorkersClient",
                    //    "config.json"
                    //),
                };

                // 按优先级查找现有配置文件，保存到相同位置
                string configPath = string.Empty;
                foreach (string path in configPaths)
                {
                    if (File.Exists(path))
                    {
                        configPath = path;
                        break;
                    }
                }

                // 如果都没有找到，默认保存到AppData
                if (string.IsNullOrEmpty(configPath))
                {
                    configPath = configPaths[1];
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };

                string json = JsonSerializer.Serialize(configData, options);
                File.WriteAllText(configPath, json);

                if (showMessage)
                {
                    MessageBox.Show("保存成功");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message);
            }
        }
        #endregion

        #region 服务器管理
        private void ComboBox1_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (comboBoxServics.SelectedItem is not ComboBoxItem item)
                return;

            _loading = true;
            configData!.current_server_id = item.Id;

            // FillServerDetails 内部也会设置 _loading = true
            FillServerDetails(item.ServerInfo);

            previousItem = item;
            _loading = false;

            // 重置保存状态，去掉"*"号
            _dirty = false;
            buttonSave.Text = "保存";

            // 同步托盘菜单
            if (_trayMenuServerGroup != null)
            {
                RefreshTrayServerMenu(_trayMenuServerGroup);
            }

            // 静默保存配置（只保存当前服务器ID）
            SaveConfigToFile(false);
        }

        private void FillServerDetails(Server server)
        {
            _loading = true; // 屏蔽 TextChanged

            textBoxServer.Text = server.server;
            textBoxListen.Text = server.listen;
            textBoxToken.Text = server.token;
            textBoxIp.Text = server.ip;
            textBoxFallback.Text = server.fallback;

            _dirty = false; // 刚加载的数据不算修改
            _loading = false;

            // 如果系统代理已启用，重新设置以使用新的监听地址
            if (checkBoxSystemProxy.Checked && !string.IsNullOrEmpty(server.listen))
            {
                SetSystemProxy(true);
            }
        }

        private void SaveCurrentServer()
        {
            if (comboBoxServics.SelectedItem is not ComboBoxItem item)
                return;

            _loading = true; // 防止触发TextChanged事件

            var server = item.ServerInfo;
            // 保存界面数据到服务器对象
            server.server = textBoxServer.Text;
            server.listen = textBoxListen.Text;
            server.token = textBoxToken.Text;
            server.ip = textBoxIp.Text;
            server.fallback = textBoxFallback.Text;

            // 刷新ComboBox显示，不触发SelectedIndexChanged事件
            int idx = comboBoxServics.SelectedIndex;
            comboBoxServics.BeginUpdate();
            comboBoxServics.Items[idx] = comboBoxServics.Items[idx];
            comboBoxServics.EndUpdate();
        }

        // 保存 last_state 状态
        private void SaveLastState()
        {
            if (configData?.last_state == null)
                return;

            configData.last_state.system_proxy_enabled = checkBoxSystemProxy.Checked;
            configData.last_state.auto_start_checked = checkBoxAutoStart.Checked;
            configData.last_state.was_running = !buttonStart.Enabled; // 如果开始按钮不可用，说明正在运行
        }
        #endregion

        #region 状态管理和界面事件
        private void FillLastState()
        {
            if (configData?.last_state == null)
                return;

            _loading = true;

            checkBoxSystemProxy.Checked = configData.last_state.system_proxy_enabled;

            checkBoxAutoStart.Checked = configData.last_state.auto_start_checked;

            // 设置隐藏启动选项
            checkBoxAutoStartHide.Checked = configData.last_state.auto_start_hide_checked;

            // 控制隐藏启动CheckBox启用状态
            checkBoxAutoStartHide.Enabled = checkBoxAutoStart.Checked;

            // 检查是否需要自动启动（上次关闭时正在运行）
            if (configData.last_state.was_running)
            {
                // 延迟2秒后自动启动服务
                var timer = new System.Windows.Forms.Timer();
                timer.Interval = 2000;
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    timer.Dispose();

                    if (buttonStart.Enabled)
                    {
                        ButtonStart_Click(this, EventArgs.Empty);

                        // 启动后检查是否需要隐藏窗体
                        if (
                            checkBoxAutoStart.Checked
                            && configData.last_state.auto_start_hide_checked
                        )
                        {
                            var hideTimer = new System.Windows.Forms.Timer();
                            hideTimer.Interval = 3000; // 3秒后检查服务状态
                            hideTimer.Tick += (hs, he) =>
                            {
                                hideTimer.Stop();
                                hideTimer.Dispose();

                                // 检查服务是否真正启动成功
                                if (!buttonStart.Enabled && buttonStop.Enabled)
                                {
                                    this.Hide();
                                    // AppendLog("开机启动模式：窗体已隐藏");
                                }
                            };
                            hideTimer.Start();
                        }
                    }
                };
                timer.Start();
            }
            else
            {
                // 智能恢复：如果停止时间很近（可能是异常退出），尝试恢复
                var timeSinceStop = DateTime.Now - configData.last_state.last_stop_time;
                if (timeSinceStop.TotalMinutes < 5 && checkBoxAutoStart.Checked)
                {
                    // 5分钟内停止且开机启动已启用，可能是异常退出，尝试恢复
                    var timer = new System.Windows.Forms.Timer();
                    timer.Interval = 2000;
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        timer.Dispose();

                        if (buttonStart.Enabled)
                        {
                            ButtonStart_Click(this, EventArgs.Empty);
                        }
                    };
                    timer.Start();
                }
            }

            _loading = false;
        }

        private void ButtonSave_Click(object sender, EventArgs e)
        {
            _loading = true; // 防止触发TextChanged事件
            SaveCurrentServer(); // 保存服务器配置
            SaveLastState(); // 保存界面状态
            SaveConfigToFile(); // 写入文件
            _dirty = false; // 标记为已保存
            buttonSave.Text = "保存";
            _loading = false;
        }

        private void AnyTextBox_TextChanged(object? sender, EventArgs e)
        {
            if (_loading)
                return; // 加载时不触发
            _dirty = true; // 标记为未保存

            buttonSave.Text = "保存*";
        }

        private void CheckBoxAutoStartHide_CheckedChanged(object sender, EventArgs e)
        {
            if (_loading)
                return;

            if (configData?.last_state != null)
            {
                configData.last_state.auto_start_hide_checked = checkBoxAutoStartHide.Checked;
                SaveConfigToFile(false);
            }

            // 开机启动是否隐藏窗体
            if (checkBoxAutoStartHide.Checked)
            {
                checkBoxAutoStart.Checked = true;
            }
        }

        private void CheckBoxSystemProxy_CheckedChanged(object sender, EventArgs e)
        {
            if (_loading)
                return;
            if (configData?.last_state == null)
                return;

            configData.last_state.system_proxy_enabled = checkBoxSystemProxy.Checked;

            // 实际设置系统代理
            SetSystemProxy(checkBoxSystemProxy.Checked);

            // 同步托盘菜单状态
            if (_trayMenuProxyItem != null)
                _trayMenuProxyItem.Checked = checkBoxSystemProxy.Checked;

            // 同步服务器子菜单状态
            if (_trayMenuServerGroup != null)
                RefreshTrayServerMenu(_trayMenuServerGroup);

            SaveConfigToFile(false);
        }

        private void CheckBoxAutoStart_CheckedChanged(object sender, EventArgs e)
        {
            if (_loading)
                return;
            if (configData?.last_state == null)
                return;

            configData.last_state.auto_start_checked = checkBoxAutoStart.Checked;

            // 控制隐藏启动CheckBox启用状态
            checkBoxAutoStartHide.Enabled = checkBoxAutoStart.Checked;

            // 实际设置开机启动
            SetAutoStart(checkBoxAutoStart.Checked);

            // 同步托盘菜单状态
            if (_trayMenuAutoStartItem != null)
                _trayMenuAutoStartItem.Checked = checkBoxAutoStart.Checked;

            // 同步服务器子菜单状态
            if (_trayMenuServerGroup != null)
                RefreshTrayServerMenu(_trayMenuServerGroup);

            SaveConfigToFile(false);
        }

        private void LinkClearLog_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            textBoxLog.Clear();
        }
        #endregion

        #region 服务器操作对话框
        // 显示输入对话框
        private string? ShowInputDialog(string title, string labelText, string defaultText)
        {
            using (var form = new Form())
            {
                form.Text = title;
                form.Size = new Size(320, 200);
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.StartPosition = FormStartPosition.CenterParent;

                var label = new Label
                {
                    Text = labelText,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Location = new Point(20, 20),
                    Size = new Size(256, 23),
                };

                var textBox = new TextBox
                {
                    Location = new Point(20, 50),
                    Size = new Size(256, 23),
                    Text = defaultText,
                };

                var buttonOK = new Button
                {
                    Text = "确定",
                    Location = new Point(80, 90),
                    Size = new Size(75, 36),
                    DialogResult = DialogResult.OK,
                    Enabled = !string.IsNullOrWhiteSpace(defaultText), // 默认文本非空才可用
                };

                var buttonCancel = new Button
                {
                    Text = "取消",
                    Location = new Point(165, 90),
                    Size = new Size(75, 36),
                    DialogResult = DialogResult.Cancel,
                };

                // 动态禁用确定按钮 + 文本为空时红色边框提示
                void UpdateButtonAndBorder()
                {
                    bool hasText = !string.IsNullOrWhiteSpace(textBox.Text);
                    buttonOK.Enabled = hasText;
                    textBox.BackColor = hasText ? SystemColors.Window : Color.MistyRose; // 空文本变淡红
                }

                textBox.TextChanged += (s, e) => UpdateButtonAndBorder();
                UpdateButtonAndBorder(); // 初始化状态

                form.Controls.AddRange(new Control[] { label, textBox, buttonOK, buttonCancel });
                form.AcceptButton = buttonOK;
                form.CancelButton = buttonCancel;

                if (form.ShowDialog() == DialogResult.OK)
                {
                    return textBox.Text;
                }
                return null;
            }
        }

        // 重命名按钮点击事件
        private void ButtonRename_Click(object? sender, EventArgs e)
        {
            if (comboBoxServics.SelectedItem is not ComboBoxItem item)
                return;

            string? newName = ShowInputDialog("重命名服务器", "请输入新的服务器名称:", item.Name);
            if (newName == null)
                return;

            _loading = true;

            // 更新服务器名称
            var server = item.ServerInfo;
            server.name = newName;
            item.Name = newName;

            // 刷新下拉框显示
            int idx = comboBoxServics.SelectedIndex;
            comboBoxServics.BeginUpdate();
            comboBoxServics.Items[idx] = comboBoxServics.Items[idx];
            comboBoxServics.EndUpdate();

            _loading = false;
            _dirty = true;
            buttonSave.Text = "保存*";

            // 同步托盘菜单
            if (_trayMenuServerGroup != null)
            {
                RefreshTrayServerMenu(_trayMenuServerGroup);
            }

            // 自动保存重命名操作
            SaveConfigToFile(false);
            _dirty = false;
            buttonSave.Text = "保存";
        }

        // 新增按钮点击事件
        private void ButtonAdd_Click(object? sender, EventArgs e)
        {
            if (comboBoxServics.SelectedItem is not ComboBoxItem currentItem)
                return;

            _loading = true;

            string? newName = ShowInputDialog(
                "新增服务器",
                "服务器名称:",
                "No." + new Random().Next(1000, 9999).ToString()
            );
            if (newName == null)
            {
                _loading = false;
                return;
            }

            // 获取当前服务器作为模板
            var currentServer = currentItem.ServerInfo;

            // 生成UUID和随机端口
            string serverId = Guid.NewGuid().ToString();
            int randomPort = GenerateRandomPort();

            // 创建新服务器（复制当前服务器参数）
            var newServer = new Server
            {
                id = serverId,
                name = newName,
                server = currentServer.server,
                listen = $"127.0.0.1:{randomPort}", // 重新生成监听端口
                token = currentServer.token,
                ip = currentServer.ip,
                fallback = currentServer.fallback,
            };

            // 添加到配置
            var newServers = configData!.servers?.ToList() ?? new List<Server>();
            newServers.Add(newServer);
            configData.servers = newServers.ToArray();

            // 添加到下拉框
            var newItem = new ComboBoxItem
            {
                Id = serverId,
                Name = newName,
                ServerInfo = newServer,
            };
            comboBoxServics.Items.Add(newItem);

            // 切换到新服务器
            comboBoxServics.SelectedItem = newItem;

            // 同步托盘菜单
            if (_trayMenuServerGroup != null)
            {
                RefreshTrayServerMenu(_trayMenuServerGroup);
            }

            // 自动保存新增操作
            SaveConfigToFile(false);
            _loading = false;
        }

        // 删除按钮点击事件
        private void ButtonDelete_Click(object? sender, EventArgs e)
        {
            if (comboBoxServics.SelectedItem is not ComboBoxItem currentItem)
                return;

            // 确认删除
            var result = MessageBox.Show(
                $"确定要删除服务器 \"{currentItem.Name}\" 吗？\n\n此操作不可撤销。",
                "确认删除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );

            if (result != DialogResult.Yes)
                return;

            _loading = true;

            // 从配置列表中删除
            var serverList = configData!.servers?.ToList() ?? new List<Server>();
            serverList.RemoveAll(s => s.id == currentItem.Id);
            configData.servers = serverList.ToArray();

            // 从下拉框中删除
            comboBoxServics.Items.Remove(currentItem);

            // 如果删除的是当前选中的服务器，需要重新选择
            if (configData.current_server_id == currentItem.Id)
            {
                // 如果还有其他服务器，选择第一个
                if (comboBoxServics.Items.Count > 0)
                {
                    comboBoxServics.SelectedIndex = 0;
                    if (comboBoxServics.SelectedItem is ComboBoxItem firstItem)
                    {
                        configData.current_server_id = firstItem.Id;
                        previousItem = firstItem;
                        FillServerDetails(firstItem.ServerInfo);
                    }
                }
                else
                {
                    // 如果没有服务器了，清空当前ID
                    configData.current_server_id = string.Empty;
                    previousItem = null;
                    // 清空所有输入框
                    ClearServerFields();
                }
            }

            _loading = false;
            _dirty = true;
            buttonSave.Text = "保存*";

            // 同步托盘菜单
            if (_trayMenuServerGroup != null)
            {
                RefreshTrayServerMenu(_trayMenuServerGroup);
            }

            // 自动保存删除操作
            SaveConfigToFile(false);
            _dirty = false;
            buttonSave.Text = "保存";
        }

        // 清空服务器字段
        private void ClearServerFields()
        {
            textBoxServer.Text = string.Empty;
            textBoxListen.Text = string.Empty;
            textBoxToken.Text = string.Empty;
            textBoxIp.Text = string.Empty;
            textBoxFallback.Text = string.Empty;
        }
        #endregion

        #region 系统设置和进程启动
        // 设置系统代理
        private void SetSystemProxy(bool enabled)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings",
                    true
                );

                if (key == null)
                    return;

                if (!enabled)
                {
                    // 真正禁用代理
                    key.SetValue("ProxyEnable", 0);
                    key.DeleteValue("ProxyServer", false);

                    RefreshSystemSettings();
                    return;
                }

                // enabled == true：切换 / 设置代理
                if (comboBoxServics.SelectedItem is not ComboBoxItem item)
                    return;

                string listen = item.ServerInfo.listen;
                // 解析监听地址 -> host 和 port
                if (!TryParseListen(listen, out var host, out var port))
                    return;

                // 重新组装监听地址
                string proxy = $"{host}:{port}";

                // 1. 先 disable（模拟用户取消勾选）
                key.SetValue("ProxyEnable", 0);
                key.DeleteValue("ProxyServer", false);

                RefreshSystemSettings();

                Thread.Sleep(50); // 给 WinINet 一个状态切换点，50ms 稳定兼容性最优

                // 2. 再 enable + 写新地址（模拟重新勾选）
                key.SetValue("ProxyEnable", 1);
                key.SetValue("ProxyServer", proxy);

                RefreshSystemSettings();
            }
            catch (Exception ex)
            {
                AppendLog($"设置系统代理失败: {ex.Message}");
            }
        }

        private bool TryParseListen(string listen, out string host, out int port)
        {
            host = "";
            port = 0;

            if (string.IsNullOrWhiteSpace(listen))
                return false;

            if (!listen.Contains("://"))
                listen = "http://" + listen;

            if (!Uri.TryCreate(listen, UriKind.Absolute, out var uri))
                return false;

            host = uri.Host;
            port = uri.Port;

            return port > 0;
        }

        // 强制刷新系统设置
        private void RefreshSystemSettings()
        {
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }

        // 设置开机启动
        private void SetAutoStart(bool enabled)
        {
            try
            {
                string appName = "zks-worker 客户端";
                string appPath = Application.ExecutablePath;

                using (
                    var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run",
                        true
                    )
                )
                {
                    if (enabled)
                    {
                        // 添加开机启动项
                        key?.SetValue(appName, $"\"{appPath}\"");
                    }
                    else
                    {
                        // 删除开机启动项
                        key?.DeleteValue(appName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"设置开机启动失败: {ex.Message}");
            }
        }

        [System.Runtime.InteropServices.DllImport("wininet.dll")]
        private static extern bool InternetSetOption(
            IntPtr hInternet,
            int dwOption,
            IntPtr lpBuffer,
            int dwBufferLength
        );

        private async void ButtonStart_Click(object sender, EventArgs e)
        {
            textBoxLog.Clear();
            KillRunningProcess();

            // 定义可执行文件优先级列表（使用时，可修改成下面任意一个名称，别忘了后缀）
            string[] exeNames = { "zks-core.exe", "zks-vpn.exe", "core.exe"};
            string exePath = string.Empty;

            // 按优先级查找可执行文件
            foreach (string exeFileName in exeNames)
            {
                string testPath = Path.Combine(Application.StartupPath, exeFileName);
                if (File.Exists(testPath))
                {
                    exePath = testPath;
                    break;
                }
            }

            // 如果没有找到任何可执行文件
            if (string.IsNullOrEmpty(exePath))
            {
                MessageBox.Show($"未找到可执行程序 {string.Join("、", exeNames)} 中任意一个");
                return;
            }

            string exeArgs;

            // 根据当前服务器配置构建参数
            if (comboBoxServics.SelectedItem is ComboBoxItem currentItem)
            {
                var server = currentItem.ServerInfo;
                var args = new List<string>();

                // 添加服务器地址参数
                if (!string.IsNullOrEmpty(server.server))
                    args.Add($"--worker wss://\"{server.server}\"");

                // 添加监听地址参数
                if (!string.IsNullOrEmpty(server?.listen))
                {
                    var endpoint = IPEndPoint.Parse(server.listen);
                    args.Add($"--bind \"{endpoint.Address}\"");
                    args.Add($"--port {endpoint.Port}");
                }

                // 添加Token参数
                if (!string.IsNullOrEmpty(server.token))
                    args.Add($"--auth-token \"{server.token}\"");

                // 添加IP参数
                if (!string.IsNullOrEmpty(server.ip))
                    args.Add($"--cf-ip \"{server.ip}\"");

                // 添加Fallback参数（也称PROXYIP）
                if (!string.IsNullOrEmpty(server.fallback))
                    args.Add($"--backup-addrs \"{server.fallback}\"");

                // 添加帮助参数
                // args.Add("-h");

                exeArgs = string.Join(" ", args);
            }
            else
            {
                exeArgs = "-h";
            }

            // ======= Process 配置 =======
            /*
            // 方案1：通过PowerShell执行外部exe
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::UTF8; & '{exePath}' {exeArgs}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            */

            // 方案2：直接执行外部exe
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = exeArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            _runningProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

            _runningProcess.OutputDataReceived += (_, e2) =>
            {
                if (!string.IsNullOrEmpty(e2.Data))
                    AppendLog(e2.Data);
            };

            _runningProcess.ErrorDataReceived += (_, e2) =>
            {
                if (!string.IsNullOrEmpty(e2.Data))
                    AppendLog(e2.Data);
            };

            _runningProcess.Start();
            _runningProcess.BeginOutputReadLine();
            _runningProcess.BeginErrorReadLine();

            // 在进程启动后再设置运行状态，确保_runningProcess不为null
            SetRunningState(true);

            await _runningProcess.WaitForExitAsync();

            _runningProcess = null;
            SetRunningState(false);

            AppendLog(
                $"{Environment.NewLine}=============== 内核进程已经结束 ==============={Environment.NewLine}"
            );
        }

        private void ButtonStop_Click(object sender, EventArgs e)
        {
            buttonStop.Enabled = false;
            KillRunningProcess();
            SetRunningState(false);
        }

        private void SetRunningState(bool running)
        {
            buttonStart.Enabled = !running;
            buttonStop.Enabled = running;

            // 运行时禁用参数编辑
            textBoxServer.ReadOnly = running;
            textBoxListen.ReadOnly = running;
            textBoxToken.ReadOnly = running;
            textBoxIp.ReadOnly = running;
            textBoxFallback.ReadOnly = running;
            comboBoxServics.Enabled = !running;
            buttonAdd.Enabled = !running; // 新增按钮
            buttonRename.Enabled = !running; // 重命名按钮
            buttonDel.Enabled = !running; // 删除按钮

            // 同步托盘菜单状态
            if (_trayMenuStartItem != null)
                _trayMenuStartItem.Enabled = !running;
            if (_trayMenuStopItem != null)
                _trayMenuStopItem.Enabled = running;

            // 刷新服务器子菜单项的状态
            if (_trayMenuServerGroup != null)
                RefreshTrayServerMenu(_trayMenuServerGroup);

            // 简化状态逻辑：基于实际进程状态
            if (configData?.last_state != null)
            {
                if (running)
                {
                    // 服务启动时设为true
                    configData.last_state.was_running = true;
                }
                else
                {
                    // 服务停止时设为false并记录停止时间
                    configData.last_state.was_running = false;
                    configData.last_state.last_stop_time = DateTime.Now;
                }

                // 静默保存到文件
                SaveConfigToFile(false);
            }
        }
        #endregion

        #region 窗体跳转
        private void linkLabelCsv_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
		{
			ShowOrActivateForm<Csv>();
		}

		private void linkLabelCidrs_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
		{
			ShowOrActivateForm<Cidrs>();
		}

		private void ShowOrActivateForm<T>() where T : Form, new()
		{
			// 1. 查找是否已打开（通用查找 T 类型的窗体）
			var existingForm = Application.OpenForms.OfType<T>().FirstOrDefault();
			if (existingForm != null)
			{
				existingForm.Activate();    // 已经有了就直接置顶
				if (existingForm.WindowState == FormWindowState.Minimized)
					existingForm.WindowState = FormWindowState.Normal;
				return;
			}

			// 2. 只有不存在时才创建
			T form = new T();
			form.StartPosition = FormStartPosition.Manual;

			// --- 核心居中计算（通用） ---
			// 理想坐标
			int x = this.Location.X + (this.Width - form.Width) / 2;
			int y = this.Location.Y + (this.Height - form.Height) / 2;
			// 获取当前屏幕边界（避开任务栏）
			var area = Screen.FromControl(this).WorkingArea;
			// x: 左右边界修正; y: 上下边界修正
			x = Math.Max(area.Left, Math.Min(x, area.Right - form.Width));
			y = Math.Max(area.Top, Math.Min(y, area.Bottom - form.Height));

			form.Location = new Point(x, y);

			// 3. 非模态显示并建立所有者关系
			form.Show(this); // 传入 this 保证子窗体浮在父窗体上方，且不阻塞操作
		}

        #endregion
    }

    #region 扩展方法和数据模型
    // TextBox 扩展方法：密码框显示/隐藏
    public static class TextBoxExtensions
    {
        public static void EnablePasswordToggle(this TextBox tb)
        {
            if (tb == null)
                return;

            tb.UseSystemPasswordChar = true;

            tb.Enter += (_, __) => tb.UseSystemPasswordChar = false;
            tb.Leave += (_, __) => tb.UseSystemPasswordChar = true;
        }
    }

    // JSON配置文件结构
    public class ConfigData
    {
        public Server[]? servers { get; set; } // 服务器列表
        public string? current_server_id { get; set; } // 当前选中的服务器ID
        public LastState? last_state { get; set; } // 上次运行时的状态
    }

    // 下拉框绑定项
    public class ComboBoxItem
    {
        public required string Id { get; set; } // 服务器ID
        public required string Name { get; set; } // 服务器名称
        public required Server ServerInfo { get; set; } // 服务器详细信息

        public override string ToString() => Name;
    }

    // 应用程序上次运行状态
    public class LastState
    {
        public bool was_running { get; set; } = false; // 上次是否正在运行
        public bool system_proxy_enabled { get; set; } = false; // 系统代理是否启用
        public bool auto_start_checked { get; set; } = false; // 开机启动是否勾选
        public int preferred_mode { get; set; } = 0; // 保留字段，兼容其他版本
        public bool auto_start_hide_checked { get; set; } = false; // 隐藏启动是否勾选
        public DateTime last_stop_time { get; set; } = DateTime.MinValue; // 上次停止时间，用于判断是否异常退出
    }

    // 服务器配置
    public class Server
    {
        public string id { get; set; } = string.Empty; // 服务器唯一标识
        public string name { get; set; } = string.Empty; // 服务器名称
        public string server { get; set; } = string.Empty; // 服务器地址
        public string listen { get; set; } = string.Empty; // 监听地址
        public string token { get; set; } = string.Empty; // 身份令牌
        public string ip { get; set; } = string.Empty; // 优选IP地址
        public string fallback { get; set; } = string.Empty; // PROXYIP/CF_FALLBACK_IPS/backup-addrs
    }
    #endregion
}
