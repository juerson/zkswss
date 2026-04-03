using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZKSWorkerGUI
{
    public partial class Csv : Form
    {
        #region 字段声明
        private DatabaseHelper? _db;
        private List<string[]> _filteredData = new List<string[]>();
        private int _totalCount = 0;
        private int _searchTotalCount = 0;
        private CancellationTokenSource? _searchCancellationTokenSource;
        private CancellationTokenSource? _debounceCancellationTokenSource;
        private bool _isSearching = false;
        private string _searchText = "";
        private string _searchColumn = "全部";
        private string _searchMode = "模糊匹配";
        private bool _isOverwriteMode = false;

        private int _currentPage = 1;
        private int _totalPages = 1;
        private const int DisplayPageSize = 500; // 分页显示的行数
        private const int SearchDelayMs = 300;

        private Dictionary<int, int> _pageOffsetCache = new Dictionary<int, int>();
        private Dictionary<int, List<string[]>> _pageDataCache =
            new Dictionary<int, List<string[]>>();
        private CancellationTokenSource? _prefetchCancellationTokenSource;
        private int _lastTotalCountForCache = 0; // 用于检测数据量变化

        private List<long>? _cachedSearchIds = null; // 搜索结果的全局 ID 列表缓存
        private Dictionary<int, string> _columnNameCache = new Dictionary<int, string>();

        private readonly string[] BuiltInUrls = new string[]
        {
            "https://hk.gh-proxy.org/https://raw.githubusercontent.com/FoolVPN-ID/Nautica/refs/heads/main/proxyList.txt",
            "https://hk.gh-proxy.org/https://raw.githubusercontent.com/papapapapdelesia/Emilia/refs/heads/main/Data/Country-ALIVE.txt",
            "https://hk.gh-proxy.org/https://raw.githubusercontent.com/tubesteer/proxy_ip/refs/heads/main/alive.txt",
        };
        #endregion

        private CancellationTokenSource? _operationCancellationTokenSource;

        #region 构造函数
        public Csv()
        {
            InitializeComponent();

            var icon = LoadIcon();
            if (icon != null)
            {
                this.Icon = icon;
            }

            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.FormClosing += Csv_FormClosing;

            InitializeControls();
            SetupDataGridView();
            EnableDoubleBuffering();
            LoadData();
        }

        private void EnableDoubleBuffering()
        {
            typeof(DataGridView)
                .GetProperty(
                    "DoubleBuffered",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                )
                ?.SetValue(dataGridView1, true);
        }
        #endregion

        #region 初始化
        private void InitializeControls()
        {
            comboBox1.Items.Clear();
            comboBox2.Items.Clear();

            comboBox1.Items.AddRange(
                new object[] { "IP", "Port", "Country", "Organization", "全部" }
            );
            comboBox1.SelectedIndex = 4;

            comboBox2.Items.AddRange(new object[] { "模糊匹配", "精准匹配" });
            comboBox2.SelectedIndex = 0;

            comboBox3.Items.Clear();
            comboBox3.Items.AddRange(new object[] { "追加写入(会去重)", "覆盖写入(先清空)" });
            comboBox3.SelectedIndex = 0;
            comboBox3.SelectedIndexChanged += ComboBox3_SelectedIndexChanged;

            button6.Click += ButtonPrevPage_Click;
            button9.Click += ButtonNextPage_Click;

            button1.Click += ButtonBuiltInDownload_Click;
            button2.Click += ButtonMerge_Click;
            button3.Click += ButtonCustomDownload_Click;
            button4.Click += ButtonRefresh_Click;
            button5.Click += ButtonDeleteSelected_Click;
            button7.Click += ButtonClearAll_Click;
            button8.Click += ButtonRemoveDuplicates_Click;
            textBox1.KeyDown += TextBox1_KeyDown;
            comboBox1.SelectedIndexChanged += Filter_Changed;
            comboBox2.SelectedIndexChanged += Filter_Changed;
        }

        private void SetupDataGridView()
        {
            dataGridView1.VirtualMode = true;
            dataGridView1.AllowUserToAddRows = false;
            dataGridView1.AllowUserToDeleteRows = false;
            dataGridView1.ReadOnly = true;
            dataGridView1.RowHeadersVisible = true;
            dataGridView1.RowHeadersWidth = 30;
            dataGridView1.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dataGridView1.ColumnHeadersHeightSizeMode =
                DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

            dataGridView1.Columns.Add(
                new DataGridViewTextBoxColumn
                {
                    Name = "ID",
                    HeaderText = "#",
                    ReadOnly = true,
                }
            );
            dataGridView1.Columns.Add(
                new DataGridViewTextBoxColumn
                {
                    Name = "IP",
                    HeaderText = "IP",
                    ReadOnly = true,
                }
            );
            dataGridView1.Columns.Add(
                new DataGridViewTextBoxColumn
                {
                    Name = "Port",
                    HeaderText = "Port",
                    ReadOnly = true,
                }
            );
            dataGridView1.Columns.Add(
                new DataGridViewTextBoxColumn
                {
                    Name = "Country",
                    HeaderText = "Country",
                    ReadOnly = true,
                }
            );
            dataGridView1.Columns.Add(
                new DataGridViewTextBoxColumn
                {
                    Name = "Organization",
                    HeaderText = "Organization",
                    ReadOnly = true,
                }
            );

            for (int i = 0; i < dataGridView1.Columns.Count; i++)
            {
                _columnNameCache[i] = dataGridView1.Columns[i].Name;
            }

            if (dataGridView1.Columns["ID"] != null)
            {
                dataGridView1.Columns["ID"].MinimumWidth = 80;
                dataGridView1.Columns["ID"].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
            }
            if (dataGridView1.Columns["IP"] != null)
            {
                dataGridView1.Columns["IP"].MinimumWidth = 180;
                dataGridView1.Columns["IP"].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
            }
            if (dataGridView1.Columns["Port"] != null)
            {
                dataGridView1.Columns["Port"].MinimumWidth = 80;
                dataGridView1.Columns["Port"].AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.AllCells;
            }
            if (dataGridView1.Columns["Country"] != null)
            {
                dataGridView1.Columns["Country"].MinimumWidth = 80;
                dataGridView1.Columns["Country"].AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.AllCells;
            }
            if (dataGridView1.Columns["Organization"] != null)
            {
                dataGridView1.Columns["Organization"].MinimumWidth = 420;
                dataGridView1.Columns["Organization"].AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.Fill;
            }

            dataGridView1.CellValueNeeded += DataGridView1_CellValueNeeded;
            dataGridView1.CellClick += DataGridView1_CellClick;
            dataGridView1.Scroll += DataGridView1_Scroll;
        }

        private Icon? LoadIcon()
        {
            try
            {
                var assembly = typeof(Csv).Assembly;
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
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(icoPath))
                {
                    return new Icon(icoPath);
                }
            }
            return null;
        }
        #endregion

        #region 工具方法
        private List<string[]> ParseCsvData(string content)
        {
            var newData = new List<string[]>();
            var lines = content.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries
            );
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var parts = line.Split(',');
                if (parts.Length >= 2)
                {
                    string[] row = new string[4];
                    row[0] = parts[0].Trim();
                    row[1] = parts[1].Trim();
                    row[2] = parts.Length > 2 ? parts[2].Trim() : "";
                    if (parts.Length > 3)
                    {
                        var orgParts = new List<string>();
                        for (int j = 3; j < parts.Length; j++)
                        {
                            orgParts.Add(parts[j]);
                        }
                        row[3] = string.Join(",", orgParts).Trim();
                    }
                    else
                    {
                        row[3] = "";
                    }
                    newData.Add(row);
                }
            }
            return newData;
        }

        private async Task InsertDataAsync(List<string[]> newData, bool isOverwrite)
        {
            _operationCancellationTokenSource = new CancellationTokenSource();
            var token = _operationCancellationTokenSource.Token;

            try
            {
                int existingCount = _db.GetTotalCount();

                await Task.Run(
                    () =>
                    {
                        if (token.IsCancellationRequested)
                            return;
                        if (isOverwrite)
                        {
                            _db.ClearAll();
                            _db.InsertOrReplaceData(newData);
                        }
                        else
                        {
                            _db.InsertOrIgnoreData(newData);
                        }
                    },
                    token
                );

                if (token.IsCancellationRequested)
                    return;

                int newCount = _db.GetTotalCount();
                int actuallyInserted = newCount - existingCount;

                _totalCount = newCount;
                ClearPageCache(); // 数据变化时清空缓存
                _totalPages = (_totalCount + DisplayPageSize - 1) / DisplayPageSize;
                _currentPage = Math.Min(_currentPage, _totalPages);
                if (_currentPage < 1)
                    _currentPage = 1;

                await LoadPageDataAsync();
                UpdateRecordCount();

                string modeText = isOverwrite ? "覆盖模式" : "追加模式(去重)";
                if (isOverwrite)
                {
                    MessageBox.Show(
                        $"{modeText}: 共处理 {newData.Count} 条数据",
                        "完成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
                else
                {
                    MessageBox.Show(
                        $"{modeText}: 共处理 {newData.Count} 条数据，实际新增 {actuallyInserted} 条",
                        "完成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
            }
            catch (OperationCanceledException)
            {
                // 操作被取消，不显示消息
            }
            finally
            {
                _operationCancellationTokenSource = null;
            }
        }
        #endregion

        #region 数据加载
        private async void LoadData()
        {
            _db = DatabaseHelper.Instance;
            _totalCount = await Task.Run(() => _db.GetTotalCount());
            _totalPages = (_totalCount + DisplayPageSize - 1) / DisplayPageSize;
            _currentPage = 1;

            if (_totalCount == 0)
            {
                _filteredData.Clear();
                dataGridView1.RowCount = 0;
                DownloadBuiltInUrls();
            }
            else
            {
                await LoadPageDataAsync();
            }

            UpdateRecordCount();
            UpdatePageButtons();
        }

        private async Task LoadPageDataAsync(bool forceReloadCount = false)
        {
            int savedFirstDisplayed = dataGridView1.FirstDisplayedScrollingRowIndex;
            dataGridView1.SuspendLayout();
            try
            {
                int totalCount = _isSearching ? _searchTotalCount : _totalCount;

                if (_isSearching && !string.IsNullOrEmpty(_searchText))
                {
                    _totalPages = CalculateTotalPages(_searchTotalCount);
                    int offset = GetPageOffset(_currentPage, _searchTotalCount, _totalPages);
                    int currentPageSize = CalculateDynamicPageSize(
                        _searchTotalCount,
                        _currentPage,
                        _totalPages
                    );

                    if (!TryGetCachedPageData(_currentPage, out var cachedPage))
                    {
                        // 使用 ID 缓存快速查询，不再执行 LIKE 搜索
                        _filteredData = await Task.Run(() =>
                            _db.GetPageDataByIds(_cachedSearchIds, offset, currentPageSize)
                        );
                        CachePageData(_currentPage, _filteredData);
                    }
                    else
                    {
                        _filteredData = cachedPage;
                    }

                    dataGridView1.RowCount = _filteredData.Count;

                    // 预取下一页（使用 ID 缓存，非常快）
                    PrefetchPageAsync(_currentPage + 1);
                }
                else
                {
                    // 非搜索模式，保持原有逻辑
                    _searchTotalCount = 0;
                    _totalPages = CalculateTotalPages(_totalCount);
                    int offset = GetPageOffset(_currentPage, _totalCount, _totalPages);
                    int currentPageSize = CalculateDynamicPageSize(
                        _totalCount,
                        _currentPage,
                        _totalPages
                    );

                    if (!TryGetCachedPageData(_currentPage, out var cachedPage))
                    {
                        _filteredData = await Task.Run(() =>
                            _db.GetPageData(offset, currentPageSize)
                        );
                        CachePageData(_currentPage, _filteredData);
                    }
                    else
                    {
                        _filteredData = cachedPage;
                    }

                    dataGridView1.RowCount = _filteredData.Count;
                    PrefetchPageAsync(_currentPage + 1);
                }

                _currentPage = Math.Min(_currentPage, _totalPages);
                if (_currentPage < 1)
                    _currentPage = 1;

                dataGridView1.Invalidate();
                UpdatePageButtons();
                UpdateRecordCount();

                if (savedFirstDisplayed >= 0 && savedFirstDisplayed < dataGridView1.RowCount)
                {
                    dataGridView1.FirstDisplayedScrollingRowIndex = savedFirstDisplayed;
                }
            }
            finally
            {
                dataGridView1.ResumeLayout();
            }
        }

        #endregion

        #region 搜索功能
        private void TextBox1_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                _ = PerformSearchAsync();
                e.SuppressKeyPress = true;
            }
        }

        private async Task PerformSearchAsync()
        {
            _searchCancellationTokenSource?.Cancel();
            _searchCancellationTokenSource = new CancellationTokenSource();

            _searchText = textBox1.Text.Trim();
            _searchColumn = comboBox1.SelectedItem?.ToString() ?? "全部";
            _searchMode = comboBox2.SelectedItem?.ToString() ?? "模糊匹配";

            _isSearching = !string.IsNullOrEmpty(_searchText);

            _filteredData.Clear();
            dataGridView1.RowCount = 0;
            UpdateRecordCount();

            if (_isSearching)
            {
                // 清除旧的搜索 ID 缓存
                _cachedSearchIds = null;
                _db.ClearSearchCache();
                ClearPageCache();

                textBox1.Enabled = false;
                await PerformSearchAsync(
                    _searchText,
                    _searchColumn,
                    _searchMode,
                    _searchCancellationTokenSource.Token
                );
            }
            else
            {
                _isSearching = false;
                _currentPage = 1;
                _totalPages = (_totalCount + DisplayPageSize - 1) / DisplayPageSize;
                await LoadPageDataAsync();
                UpdateRecordCount();
            }
        }

        private void Filter_Changed(object? sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_searchText))
            {
                _debounceCancellationTokenSource?.Cancel();
                _debounceCancellationTokenSource = new CancellationTokenSource();

                Task.Delay(SearchDelayMs, _debounceCancellationTokenSource.Token)
                    .ContinueWith(
                        t =>
                        {
                            if (!t.IsCanceled && !t.IsFaulted)
                            {
                                this.Invoke(async () => await PerformSearchAsync());
                            }
                        },
                        CancellationToken.None
                    );
            }
        }

        private async Task PerformSearchAsync(
            string searchText,
            string column,
            string matchMode,
            CancellationToken token
        )
        {
            try
            {
                // 第一步：获取所有匹配的 ID（只执行一次慢查询）
                var searchIds = await Task.Run(
                    () => _db.GetSearchIds(searchText, column, matchMode),
                    token
                );

                if (token.IsCancellationRequested)
                    return;

                // 第二步：缓存 ID 列表
                _cachedSearchIds = searchIds;
                _searchTotalCount = searchIds.Count;
                _totalPages = CalculateTotalPages(_searchTotalCount);
                _currentPage = 1;

                // 第三步：预加载前 5 页数据（使用 ID 缓存快速查询）
                var pagesToPreload = Math.Min(5, _totalPages);
                var preloadTasks = new List<Task>();

                for (int page = 1; page <= pagesToPreload; page++)
                {
                    int p = page; // 闭包变量
                    preloadTasks.Add(
                        Task.Run(
                            () =>
                            {
                                if (token.IsCancellationRequested)
                                    return;

                                int offset = GetPageOffset(p, _searchTotalCount, _totalPages);
                                int pageSize = CalculateDynamicPageSize(
                                    _searchTotalCount,
                                    p,
                                    _totalPages
                                );

                                var pageData = _db.GetPageDataByIds(
                                    _cachedSearchIds,
                                    offset,
                                    pageSize
                                );
                                CachePageData(p, pageData);
                            },
                            token
                        )
                    );
                }

                await Task.WhenAll(preloadTasks);

                if (!token.IsCancellationRequested)
                {
                    if (this.InvokeRequired)
                    {
                        this.Invoke(
                            new Action(() =>
                            {
                                _filteredData = TryGetCachedPageData(1, out var firstPage)
                                    ? firstPage
                                    : new List<string[]>();
                                dataGridView1.RowCount = _filteredData.Count;
                                UpdateRecordCount();
                                UpdatePageButtons();
                                textBox1.Enabled = true;
                            })
                        );
                    }
                    else
                    {
                        _filteredData = TryGetCachedPageData(1, out var firstPage)
                            ? firstPage
                            : new List<string[]>();
                        dataGridView1.RowCount = _filteredData.Count;
                        UpdateRecordCount();
                        UpdatePageButtons();
                        textBox1.Enabled = true;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (this.InvokeRequired)
                {
                    this.Invoke(
                        new Action(() =>
                        {
                            MessageBox.Show(
                                $"搜索出错：{ex.Message}",
                                "错误",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error
                            );
                            textBox1.Enabled = true;
                        })
                    );
                }
                else
                {
                    MessageBox.Show(
                        $"搜索出错：{ex.Message}",
                        "错误",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    textBox1.Enabled = true;
                }
            }
        }
        #endregion

        #region 下载功能
        private async void DownloadBuiltInUrls()
        {
            button1.Enabled = false;
            button1.Text = "加载中...";

            try
            {
                var newData = new List<string[]>();
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(30);

                for (int i = 0; i < BuiltInUrls.Length; i++)
                {
                    try
                    {
                        var response = await client.GetStringAsync(BuiltInUrls[i]);
                        var parsedData = ParseCsvData(response);

                        // 去重逻辑，与原来相同
                        foreach (var row in parsedData)
                        {
                            bool exists = newData.Any(r =>
                                r.Length > 0 && r[0] == row[0] && r.Length > 1 && r[1] == row[1]
                            );
                            if (!exists || i == 0)
                            {
                                if (exists)
                                {
                                    var existing = newData.FirstOrDefault(r =>
                                        r.Length > 0
                                        && r[0] == row[0]
                                        && r.Length > 1
                                        && r[1] == row[1]
                                    );
                                    if (existing != null)
                                    {
                                        newData.Remove(existing);
                                    }
                                }
                                newData.Add(row);
                            }
                        }
                    }
                    catch { }
                }

                if (newData.Count > 0)
                {
                    button1.Text = "写入中...";
                    await InsertDataAsync(newData, _isOverwriteMode);
                }
            }
            catch { }
            finally
            {
                button1.Enabled = true;
                button1.Text = "下载更新";
            }
        }

        private async void ButtonBuiltInDownload_Click(object sender, EventArgs e)
        {
            DownloadBuiltInUrls();
        }

        private async void ButtonCustomDownload_Click(object sender, EventArgs e)
        {
            string url = textBoxUrl.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show(
                    "请输入下载链接",
                    "提示",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            button3.Enabled = false;
            button3.Text = "下载中...";

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                var response = await client.GetStringAsync(url);

                var newData = ParseCsvData(response);

                if (newData.Count > 0)
                {
                    button3.Text = "写入中...";
                    await InsertDataAsync(newData, _isOverwriteMode);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"下载失败: {ex.Message}",
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                button3.Enabled = true;
                button3.Text = "自定义下载";
            }
        }
        #endregion

        #region 合并功能
        private async void ButtonMerge_Click(object sender, EventArgs e)
        {
            using (var openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "CSV数据|*.csv;*.txt|所有文件|*.*";
                openFileDialog.Multiselect = true;
                openFileDialog.Title = "选择要合并的CSV数据";

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    button2.Enabled = false;
                    button2.Text = "合并中...";

                    var newData = new List<string[]>();
                    foreach (var file in openFileDialog.FileNames)
                    {
                        try
                        {
                            var content = await Task.Run(() =>
                                File.ReadAllText(file, Encoding.UTF8)
                            );
                            var parsedData = ParseCsvData(content);
                            newData.AddRange(parsedData);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(
                                $"合并数据失败 {file}: {ex.Message}",
                                "错误",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error
                            );
                        }
                    }

                    if (newData.Count > 0)
                    {
                        await InsertDataAsync(newData, _isOverwriteMode);
                    }

                    button2.Enabled = true;
                    button2.Text = "合并数据";
                }
            }
        }
        #endregion

        #region 删除功能
        private async void ButtonDeleteSelected_Click(object sender, EventArgs e)
        {
            if (dataGridView1.SelectedRows.Count == 0)
            {
                MessageBox.Show(
                    "请先选中要删除的行",
                    "提示",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }

            int selectedCount = dataGridView1.SelectedRows.Count;
            if (selectedCount > 500)
            {
                MessageBox.Show(
                    "选中行数过多，请选择小于等于500行进行删除",
                    "提示",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            var result = MessageBox.Show(
                $"确定要删除选中的 {selectedCount} 行数据吗？",
                "确认删除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                button5.Enabled = false;
                button5.Text = "删除中...";

                var itemsToDelete = new List<(string ip, string port)>();

                foreach (DataGridViewRow row in dataGridView1.SelectedRows)
                {
                    int rowIndex = row.Index;
                    if (rowIndex < _filteredData.Count)
                    {
                        var rowData = _filteredData[rowIndex];
                        if (rowData.Length >= 2)
                        {
                            itemsToDelete.Add((rowData[0], rowData[1]));
                        }
                    }
                }

                if (itemsToDelete.Count > 0)
                {
                    _operationCancellationTokenSource = new CancellationTokenSource();
                    var token = _operationCancellationTokenSource.Token;

                    try
                    {
                        await Task.Run(() => _db.DeleteByIpPortList(itemsToDelete), token);

                        if (token.IsCancellationRequested)
                            return;

                        _totalCount = _db.GetTotalCount();
                        ClearPageCache(); // 数据变化时清空缓存
                        _totalPages = (_totalCount + DisplayPageSize - 1) / DisplayPageSize;
                        _currentPage = Math.Min(_currentPage, _totalPages);
                        if (_currentPage < 1)
                            _currentPage = 1;

                        if (_isSearching)
                        {
                            await PerformSearchAsync();
                        }
                        else
                        {
                            await LoadPageDataAsync();
                        }
                        UpdateRecordCount();

                        MessageBox.Show(
                            "删除成功",
                            "完成",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information
                        );
                    }
                    catch (OperationCanceledException)
                    {
                        // 操作被取消
                    }
                    finally
                    {
                        _operationCancellationTokenSource = null;
                    }
                }

                button5.Enabled = true;
                button5.Text = "删除选中行";
            }
        }

        private async void ButtonClearAll_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "确定要清空所有数据吗？此操作不可恢复！",
                "确认清空",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );

            if (result == DialogResult.Yes)
            {
                button7.Enabled = false;
                button7.Text = "清空中...";

                await Task.Run(() => _db.ClearAll());

                _totalCount = 0;
                ClearPageCache(); // 数据变化时清空缓存
                _filteredData.Clear();
                dataGridView1.RowCount = 0;
                UpdateRecordCount();

                MessageBox.Show(
                    "数据已清空",
                    "完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                button7.Enabled = true;
                button7.Text = "清空全部";
            }
        }

        private async void ButtonRemoveDuplicates_Click(object sender, EventArgs e)
        {
            int beforeCount = _db.GetTotalCount();

            var result = MessageBox.Show(
                "确定要删除重复数据吗？\n只保留每个IP:Port组合的首条记录，其它重复的记录将被删除。",
                "确认清理",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                button8.Enabled = false;
                button8.Text = "清理中...";

                await Task.Run(() => _db.RemoveDuplicates());

                int afterCount = _db.GetTotalCount();
                int removedCount = beforeCount - afterCount;

                _totalCount = afterCount;
                ClearPageCache(); // 数据变化时清空缓存

                await LoadPageDataAsync();
                UpdateRecordCount();

                MessageBox.Show(
                    $"删除完成，共删除 {removedCount} 条重复记录",
                    "完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                button8.Enabled = true;
                button8.Text = "删除重复行";
            }
        }
        #endregion

        #region DataGridView事件
        private void DataGridView1_Scroll(object sender, ScrollEventArgs e)
        {
            return;
        }

        private void DataGridView1_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _filteredData.Count)
            {
                e.Value = "";
                return;
            }

            var row = _filteredData[e.RowIndex];
            string colName = dataGridView1.Columns[e.ColumnIndex].Name;

            // 计算全局 ID（使用缓存获取前面所有页的数据量）
            int globalIndex = e.RowIndex;
            if (_currentPage > 1)
            {
                int totalCount = _isSearching ? _searchTotalCount : _totalCount;
                int totalPages = CalculateTotalPages(totalCount);
                globalIndex += GetPageOffset(_currentPage, totalCount, totalPages);
            }

            switch (colName)
            {
                case "ID":
                    e.Value = globalIndex + 1;
                    break;
                case "IP":
                    e.Value = row.Length > 0 ? row[0] : "";
                    break;
                case "Port":
                    e.Value = row.Length > 1 ? row[1] : "";
                    break;
                case "Country":
                    e.Value = row.Length > 2 ? row[2] : "";
                    break;
                case "Organization":
                    e.Value = row.Length > 3 ? row[3] : "";
                    break;
                default:
                    e.Value = "";
                    break;
            }
        }

        private void DataGridView1_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && e.ColumnIndex < 0)
            {
                if (e.RowIndex < _filteredData.Count)
                {
                    var row = _filteredData[e.RowIndex];
                    string ip = row.Length > 0 ? row[0] : "";
                    string port = row.Length > 1 ? row[1] : "";
                    if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(port))
                    {
                        string ipPort = $"{ip}:{port}";
                        Clipboard.SetText(ipPort);
                        MessageBox.Show(
                            $"已复制: {ipPort}",
                            "复制成功",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information
                        );
                    }
                }
            }
        }
        #endregion

        #region 其他事件
        private void ComboBox3_SelectedIndexChanged(object? sender, EventArgs e)
        {
            _isOverwriteMode = (comboBox3.SelectedIndex == 1);
        }

        private void ButtonRefresh_Click(object sender, EventArgs e)
        {
            LoadData();
        }

        private async void ButtonPrevPage_Click(object sender, EventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                await LoadPageDataAsync();
                UpdateRecordCount();
                UpdatePageButtons();
            }
        }

        private async void ButtonNextPage_Click(object sender, EventArgs e)
        {
            if (_currentPage < _totalPages)
            {
                _currentPage++;
                await LoadPageDataAsync();
                UpdateRecordCount();
                UpdatePageButtons();
            }
        }

        private void UpdatePageButtons()
        {
            button6.Enabled = _currentPage > 1;
            button9.Enabled = _currentPage < _totalPages;
        }

        private void Csv_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _operationCancellationTokenSource?.Cancel();
        }

        private void UpdateRecordCount()
        {
            int total = _isSearching ? _searchTotalCount : _totalCount;
            int currentPageSize = _filteredData.Count;
            label1.Text =
                $"第{_currentPage}页/共{_totalPages}页 | 当页{currentPageSize}条/共{total}条";
        }
        #endregion

        // 计算动态每页行数（均衡分页）
        private int CalculateDynamicPageSize(int totalCount, int currentPage, int totalPages)
        {
            if (totalCount <= 0)
                return 0;
            if (totalPages <= 1)
                return totalCount;

            // 基础每页行数
            int basePageSize = totalCount / totalPages;
            int remainder = totalCount % totalPages;

            // 前 remainder 页多显示 1 行，让数据更均匀
            int pageSize = currentPage <= remainder ? basePageSize + 1 : basePageSize;
            // 限制每页最多 500 行
            if (pageSize > DisplayPageSize)
            {
                pageSize = DisplayPageSize;
            }
            return pageSize;
        }

        // 计算总页数（考虑均衡分页）
        private int CalculateTotalPages(int totalCount)
        {
            if (totalCount <= 0)
                return 1;
            if (totalCount <= DisplayPageSize)
            {
                return 1;
            }
            // 基础页数
            int basePages = (totalCount + DisplayPageSize - 1) / DisplayPageSize;

            // 尝试减少页数，让每页更均匀
            for (int pages = basePages; pages >= 1; pages--)
            {
                int basePageSize = totalCount / pages;
                int remainder = totalCount % pages;
                int lastPageData =
                    totalCount - (pages - 1) * basePageSize - Math.Min(remainder, pages - 1);
                int firstPageSize = 1 <= remainder ? basePageSize + 1 : basePageSize;

                if (firstPageSize > DisplayPageSize)
                    firstPageSize = DisplayPageSize;

                // 最后一页数据不低于第一页的一半
                if (lastPageData >= firstPageSize / 2 || pages == 1)
                {
                    return pages;
                }
            }
            return basePages;
        }

        // 获取页偏移量（带缓存）
        private int GetPageOffset(int pageNumber, int totalCount, int totalPages)
        {
            // 检查数据量是否变化，变化则清空缓存
            if (_lastTotalCountForCache != totalCount)
            {
                _pageOffsetCache.Clear();
                _lastTotalCountForCache = totalCount;
            }
            // 从缓存获取
            if (_pageOffsetCache.TryGetValue(pageNumber, out int offset))
            {
                return offset;
            }
            // 计算偏移量
            offset = 0;
            for (int i = 1; i < pageNumber; i++)
            {
                offset += CalculateDynamicPageSize(totalCount, i, totalPages);
            }
            // 存入缓存
            _pageOffsetCache[pageNumber] = offset;
            return offset;
        }

        // 清空缓存（数据变化时调用）
        private void ClearPageCache()
        {
            _pageOffsetCache.Clear();
            _pageDataCache.Clear();
            _lastTotalCountForCache = 0;
            _cachedSearchIds = null; // 清除搜索 ID 缓存

            if (_prefetchCancellationTokenSource != null)
            {
                _prefetchCancellationTokenSource.Cancel();
                _prefetchCancellationTokenSource.Dispose();
                _prefetchCancellationTokenSource = null;
            }
        }

        private void CachePageData(int pageNumber, List<string[]> data)
        {
            if (data == null)
                return;
            _pageDataCache[pageNumber] = data;
        }

        private bool TryGetCachedPageData(int pageNumber, out List<string[]>? data)
        {
            return _pageDataCache.TryGetValue(pageNumber, out data);
        }

        private void PrefetchPageAsync(int pageNumber)
        {
            if (pageNumber < 1 || pageNumber > _totalPages)
                return;
            if (TryGetCachedPageData(pageNumber, out _))
                return;

            _prefetchCancellationTokenSource?.Cancel();
            _prefetchCancellationTokenSource?.Dispose();
            _prefetchCancellationTokenSource = new CancellationTokenSource();
            var token = _prefetchCancellationTokenSource.Token;

            Task.Run(
                () =>
                {
                    try
                    {
                        if (token.IsCancellationRequested)
                            return;

                        int totalCount = _isSearching ? _searchTotalCount : _totalCount;
                        int offset = GetPageOffset(pageNumber, totalCount, _totalPages);
                        int pageSize = CalculateDynamicPageSize(
                            totalCount,
                            pageNumber,
                            _totalPages
                        );

                        List<string[]> pageData;
                        if (_isSearching)
                        {
                            pageData = _db.SearchPageData(
                                _searchText,
                                _searchColumn,
                                _searchMode,
                                offset,
                                pageSize
                            );
                        }
                        else
                        {
                            pageData = _db.GetPageData(offset, pageSize);
                        }

                        if (token.IsCancellationRequested)
                            return;
                        CachePageData(pageNumber, pageData);
                    }
                    catch { }
                },
                token
            );
        }
    }
}
