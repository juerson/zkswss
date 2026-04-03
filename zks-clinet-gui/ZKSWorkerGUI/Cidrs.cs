using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZKSWorkerGUI
{
    public partial class Cidrs : Form
    {
        #region 字段声明
        private CidrDatabaseHelper? _db;
        private List<string[]> _filteredData = new List<string[]>();
        private int _totalCount = 0;
        private int _currentPage = 1;
        private int _totalPages = 1;
        private const int DisplayPageSize = 500;
        private bool _isOverwriteMode = false;
        private CancellationTokenSource? _operationCancellationTokenSource;
        private Dictionary<int, int> _pageOffsetCache = new Dictionary<int, int>();
        private Dictionary<int, List<string[]>> _pageDataCache =
            new Dictionary<int, List<string[]>>();
        private CancellationTokenSource? _prefetchCancellationTokenSource;
        private int _lastTotalCountForCache = 0;
        private string proxyUrl = "https://hk.gh-proxy.org/";

        // 搜索相关
        private string _searchKeyword = "";
        private int _searchTotalCount = 0;
        private bool _isSearching = false;
        private List<long>? _cachedSearchIds = null; // 搜索 ID 缓存

        // GitHub 仓库配置
        private readonly (string owner, string repo, string fileName)[] _githubRepos = new[]
        {
            ("compassvpn", "cf-tools", "all_cdn_v4.txt"),
        };
        #endregion

        public Cidrs()
        {
            InitializeComponent();
            var icon = LoadIcon();
            if (icon != null)
            {
                this.Icon = icon;
            }

            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.FormClosing += Cidrs_FormClosing;

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

        #region 初始化
        private void InitializeControls()
        {
            comboBoxMode.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBoxMode.Items.Clear();
            comboBoxMode.Items.AddRange(new object[] { "追加写入(会去重)", "覆盖写入(先清空)" });
            comboBoxMode.SelectedIndex = 0;
            comboBoxMode.SelectedIndexChanged += ComboBox1_SelectedIndexChanged;

            textBoxFilter.KeyDown += TextBox2_KeyDown;

            btnCustomDown.Click += ButtonBuiltInDownload_Click;
            btnMergeCidrs.Click += ButtonMerge_Click;
            btnUrlDown.Click += ButtonCustomDownload_Click;
            btnCheckedDelete.Click += ButtonDeleteSelected_Click;
            btnDeduplicate.Click += ButtonRemoveDuplicates_Click;
            btnPrev.Click += ButtonPrevPage_Click;
            btnNext.Click += ButtonNextPage_Click;
        }

        private async void TextBox2_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                await PerformSearchAsync();
                e.SuppressKeyPress = true;
            }
        }

        // 使用搜索 ID 缓存策略 + 多页预加载
        private async Task PerformSearchAsync()
        {
            _searchKeyword = textBoxFilter.Text.Trim();
            _isSearching = !string.IsNullOrEmpty(_searchKeyword);
            _currentPage = 1;

            _filteredData.Clear();
            dataGridView1.RowCount = 0;
            UpdateRecordCount();

            // 清理缓存
            ClearPageCache();
            _cachedSearchIds = null;
            _db.ClearSearchCache();

            if (_isSearching)
            {
                textBoxFilter.Enabled = false;
                try
                {
                    // 第一步：获取所有匹配的 ID（只执行一次慢查询）
                    var searchIds = await Task.Run(() => _db.GetSearchIds(_searchKeyword));

                    _cachedSearchIds = searchIds;
                    _searchTotalCount = searchIds.Count;
                    _totalPages = CalculateTotalPages(_searchTotalCount);

                    // 第二步：预加载前 5 页数据（使用 ID 缓存快速查询）
                    int prefetchCount = Math.Min(5, _totalPages);
                    for (int page = 1; page <= prefetchCount; page++)
                    {
                        int offset = (page - 1) * DisplayPageSize;
                        var pageData = await Task.Run(() =>
                            _db.GetPageDataByIds(_cachedSearchIds, offset, DisplayPageSize)
                        );

                        if (pageData.Count > 0)
                        {
                            CachePageData(page, pageData);

                            // 第一页直接显示
                            if (page == 1)
                            {
                                _filteredData = pageData;
                            }
                        }
                    }

                    dataGridView1.RowCount = _filteredData.Count;

                    // 第三步：后台预取后续页面
                    PrefetchPageAsync(prefetchCount + 1);
                }
                finally
                {
                    textBoxFilter.Enabled = true;
                }
            }
            else
            {
                _searchTotalCount = 0;
                _totalPages = CalculateTotalPages(_totalCount);
                await LoadPageDataAsync();
            }

            UpdatePageButtons();
            UpdateRecordCount();
        }

        private void SetupDataGridView()
        {
            dataGridView1.VirtualMode = true;
            dataGridView1.AllowUserToAddRows = false;
            dataGridView1.AllowUserToDeleteRows = false;
            dataGridView1.ReadOnly = false;
            dataGridView1.RowHeadersVisible = true;
            dataGridView1.RowHeadersWidth = 30;
            dataGridView1.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dataGridView1.ColumnHeadersHeightSizeMode =
                DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

            var idCol = new DataGridViewTextBoxColumn
            {
                Name = "ID",
                HeaderText = "#",
                ReadOnly = true,
            };
            var cidrCol = new DataGridViewTextBoxColumn
            {
                Name = "CIDR",
                HeaderText = "CIDR 地址",
                ReadOnly = true,
            };
            var remarkCol = new DataGridViewTextBoxColumn
            {
                Name = "Remark",
                HeaderText = "备注",
                ReadOnly = false,
            };

            dataGridView1.Columns.Add(idCol);
            dataGridView1.Columns.Add(cidrCol);
            dataGridView1.Columns.Add(remarkCol);

            if (dataGridView1.Columns["ID"] != null)
            {
                dataGridView1.Columns["ID"].MinimumWidth = 50;
                dataGridView1.Columns["ID"].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
            }
            if (dataGridView1.Columns["CIDR"] != null)
            {
                dataGridView1.Columns["CIDR"].MinimumWidth = 100;
                dataGridView1.Columns["CIDR"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            }
            if (dataGridView1.Columns["Remark"] != null)
            {
                dataGridView1.Columns["Remark"].MinimumWidth = 470;
                dataGridView1.Columns["Remark"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            }

            dataGridView1.CellValueNeeded += DataGridView1_CellValueNeeded;
            dataGridView1.CellValuePushed += DataGridView1_CellValuePushed;
            dataGridView1.CellClick += DataGridView1_CellClick;
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

        #region 数据加载
        private async void LoadData()
        {
            _db = CidrDatabaseHelper.Instance;
            _searchKeyword = textBoxFilter.Text.Trim();
            _isSearching = !string.IsNullOrEmpty(_searchKeyword);

            if (_isSearching)
            {
                await PerformSearchAsync();
            }
            else
            {
                _totalCount = await Task.Run(() => _db.GetTotalCount());
                _totalPages = CalculateTotalPages(_totalCount);
                _currentPage = 1;

                if (_totalCount == 0)
                {
                    await DownloadBuiltInDataAsync();
                }
                else
                {
                    await LoadPageDataAsync();
                }
            }

            UpdateRecordCount();
            UpdatePageButtons();
        }

        private async Task DownloadBuiltInDataAsync()
        {
            btnCustomDown.Enabled = false;
            btnCustomDown.Text = "首次加载...";

            try
            {
                _operationCancellationTokenSource = new CancellationTokenSource();
                var token = _operationCancellationTokenSource.Token;

                var allCidrs = new List<string>();

                var builtInUrls = await GetBuiltInUrlsAsync();
                for (int i = 0; i < builtInUrls.Count; i++)
                {
                    try
                    {
                        var cidrs = await DownloadAndParseCidrAsync(builtInUrls[i], token);
                        allCidrs.AddRange(cidrs);
                    }
                    catch { }
                }

                allCidrs = allCidrs.Distinct().ToList();

                if (allCidrs.Count > 0)
                {
                    btnCustomDown.Text = "写入中...";
                    await InsertCidrDataAsync(allCidrs);
                }
            }
            catch { }
            finally
            {
                btnCustomDown.Enabled = true;
                btnCustomDown.Text = "内置源下载";
            }
        }

        // 使用 ID 缓存
        private async Task LoadPageDataAsync()
        {
            int savedFirstDisplayed = dataGridView1.FirstDisplayedScrollingRowIndex;
            dataGridView1.SuspendLayout();
            try
            {
                int totalCount;

                if (_isSearching)
                {
                    totalCount = _searchTotalCount;
                    _totalPages = CalculateTotalPages(_searchTotalCount);
                }
                else
                {
                    totalCount = _totalCount;
                    _totalPages = CalculateTotalPages(_totalCount);
                }

                _currentPage = Math.Min(_currentPage, _totalPages);
                if (_currentPage < 1)
                    _currentPage = 1;

                int offset = GetPageOffset(_currentPage, totalCount, _totalPages);
                int currentPageSize = CalculateDynamicPageSize(
                    totalCount,
                    _currentPage,
                    _totalPages
                );

                if (!TryGetCachedPageData(_currentPage, out var cachedPage))
                {
                    if (_isSearching && _cachedSearchIds != null)
                    {
                        // 使用 ID 缓存快速查询
                        _filteredData = await Task.Run(() =>
                            _db.GetPageDataByIds(_cachedSearchIds, offset, currentPageSize)
                        );
                    }
                    else if (_isSearching)
                    {
                        _filteredData = await Task.Run(() =>
                            _db.SearchPageData(_searchKeyword, offset, currentPageSize)
                        );
                    }
                    else
                    {
                        _filteredData = await Task.Run(() =>
                            _db.GetPageData(offset, currentPageSize)
                        );
                    }

                    CachePageData(_currentPage, _filteredData);
                }
                else
                {
                    _filteredData = cachedPage;
                }

                dataGridView1.RowCount = _filteredData.Count;
                PrefetchPageAsync(_currentPage + 1);

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

        #region 下载功能
        private async Task<List<string>> GetBuiltInUrlsAsync()
        {
            var urls = new List<string> { "https://www.cloudflare.com/ips-v4" };

            var tasks = _githubRepos.Select(repo =>
                GetLatestReleaseUrlAsync(repo.owner, repo.repo, repo.fileName)
            );
            var releaseUrls = await Task.WhenAll(tasks);

            foreach (var url in releaseUrls)
            {
                if (!string.IsNullOrEmpty(url))
                {
                    urls.Add(url);
                }
            }

            return urls;
        }

        private async Task<string?> GetLatestReleaseUrlAsync(
            string owner,
            string repo,
            string fileName
        )
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

                string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
                var response = await client.GetStringAsync(apiUrl);

                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                if (root.TryGetProperty("tag_name", out var tagElement))
                {
                    string tagName = tagElement.GetString() ?? "";
                    if (!string.IsNullOrEmpty(tagName))
                    {
                        return $"{proxyUrl}https://github.com/{owner}/{repo}/releases/download/{tagName}/{fileName}";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"获取 {owner}/{repo} 发布链接失败：{ex.Message}"
                );
            }
            return null;
        }

        private async Task<List<string>> DownloadAndParseCidrAsync(
            string url,
            CancellationToken token
        )
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var response = await client.GetStringAsync(url, token);
            return ParseAndConvertCidrData(response);
        }

        private async void ButtonBuiltInDownload_Click(object sender, EventArgs e)
        {
            btnCustomDown.Enabled = false;
            btnCustomDown.Text = "下载中...";

            try
            {
                _operationCancellationTokenSource = new CancellationTokenSource();
                var token = _operationCancellationTokenSource.Token;

                var allCidrs = new List<string>();
                var builtInUrls = await GetBuiltInUrlsAsync();

                for (int i = 0; i < builtInUrls.Count; i++)
                {
                    try
                    {
                        var cidrs = await DownloadAndParseCidrAsync(builtInUrls[i], token);
                        allCidrs.AddRange(cidrs);
                    }
                    catch { }
                }

                allCidrs = allCidrs.Distinct().ToList();

                if (allCidrs.Count > 0)
                {
                    btnCustomDown.Text = "写入中...";
                    await InsertCidrDataAsync(allCidrs);
                }

                MessageBox.Show(
                    $"内置源下载完成，共获取 {allCidrs.Count} 条 CIDR 记录",
                    "完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"下载失败：{ex.Message}",
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                btnCustomDown.Enabled = true;
                btnCustomDown.Text = "内置源下载";
            }
        }

        private async void ButtonCustomDownload_Click(object sender, EventArgs e)
        {
            string url = textBoxCustomUrl.Text.Trim();
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

            btnUrlDown.Enabled = false;
            btnUrlDown.Text = "下载中...";

            try
            {
                _operationCancellationTokenSource = new CancellationTokenSource();
                var token = _operationCancellationTokenSource.Token;

                var cidrs = await DownloadAndParseCidrAsync(url, token);

                if (cidrs.Count > 0)
                {
                    btnUrlDown.Text = "写入中...";
                    await InsertCidrDataAsync(cidrs);
                    MessageBox.Show(
                        $"下载完成，共获取 {cidrs.Count} 条 CIDR 记录",
                        "完成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
                else
                {
                    MessageBox.Show(
                        "未获取到有效数据",
                        "提示",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"下载失败：{ex.Message}",
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                btnUrlDown.Enabled = true;
                btnUrlDown.Text = "下载";
            }
        }

        private async void ButtonMerge_Click(object sender, EventArgs e)
        {
            using (var openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "文本文件|*.txt|所有文件|*.*";
                openFileDialog.Multiselect = true;
                openFileDialog.Title = "选择要导入的 CIDR 文件";

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    btnMergeCidrs.Enabled = false;
                    btnMergeCidrs.Text = "导入中...";

                    var allCidrs = new List<string>();
                    foreach (var file in openFileDialog.FileNames)
                    {
                        try
                        {
                            var content = await Task.Run(() =>
                                File.ReadAllText(file, Encoding.UTF8)
                            );
                            var cidrs = ParseAndConvertCidrData(content);
                            allCidrs.AddRange(cidrs);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(
                                $"读取文件失败 {file}: {ex.Message}",
                                "错误",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error
                            );
                        }
                    }

                    allCidrs = allCidrs.Distinct().ToList();

                    if (allCidrs.Count > 0)
                    {
                        await InsertCidrDataAsync(allCidrs);
                        MessageBox.Show(
                            $"导入完成，共处理 {allCidrs.Count} 条 CIDR 记录",
                            "完成",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information
                        );
                    }

                    btnMergeCidrs.Enabled = true;
                    btnMergeCidrs.Text = "导入本地文件";
                }
            }
        }

        private async Task InsertCidrDataAsync(List<string> cidrs)
        {
            var existingCount = _db.GetTotalCount();
            var dataList = cidrs.Select(c => new string[] { c, "" }).ToList();

            await Task.Run(() =>
            {
                if (_isOverwriteMode)
                {
                    _db.ClearAll();
                }
                _db.InsertOrIgnoreCidrs(dataList);
            });

            int newCount = _db.GetTotalCount();
            int actuallyInserted = newCount - existingCount;

            _totalCount = newCount;
            ClearPageCache();
            _totalPages = (_totalCount + DisplayPageSize - 1) / DisplayPageSize;
            _currentPage = Math.Min(_currentPage, _totalPages);
            if (_currentPage < 1)
                _currentPage = 1;

            await LoadPageDataAsync();
            UpdateRecordCount();

            string modeText = _isOverwriteMode ? "覆盖模式" : "追加模式 (去重)";
            MessageBox.Show(
                $"{modeText}: 共处理 {cidrs.Count} 条数据，实际新增 {actuallyInserted} 条",
                "完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        #endregion

        #region 删除和去重
        private async void btnClearAll_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "确定要清空所有数据吗？",
                "确认清空",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                await Task.Run(() => _db.ClearAll());
                _totalCount = _db.GetTotalCount();
                ClearPageCache();
                await LoadPageDataAsync();
                UpdateRecordCount();

                MessageBox.Show(
                    "清空完成",
                    "完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
        }

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
            var result = MessageBox.Show(
                $"确定要删除选中的 {selectedCount} 行数据吗？",
                "确认删除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                var idsToDelete = new List<int>();

                foreach (DataGridViewRow row in dataGridView1.SelectedRows)
                {
                    int rowIndex = row.Index;
                    if (rowIndex < _filteredData.Count)
                    {
                        var rowData = _filteredData[rowIndex];
                        if (rowData.Length > 0 && int.TryParse(rowData[0], out int id))
                        {
                            idsToDelete.Add(id);
                        }
                    }
                }

                if (idsToDelete.Count > 0)
                {
                    await Task.Run(() => _db.DeleteByIdList(idsToDelete));
                    _totalCount = _db.GetTotalCount();
                    ClearPageCache();
                    _totalPages = (_totalCount + DisplayPageSize - 1) / DisplayPageSize;
                    _currentPage = Math.Min(_currentPage, _totalPages);
                    if (_currentPage < 1)
                        _currentPage = 1;

                    await LoadPageDataAsync();
                    UpdateRecordCount();
                    MessageBox.Show(
                        "删除成功",
                        "完成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
            }
        }

        private async void ButtonRemoveDuplicates_Click(object sender, EventArgs e)
        {
            int beforeCount = _db.GetTotalCount();
            var result = MessageBox.Show(
                "确定要删除重复数据吗？\n只保留每个 CIDR 的首条记录。",
                "确认清理",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                btnDeduplicate.Enabled = false;
                btnDeduplicate.Text = "清理中...";

                await Task.Run(() => _db.RemoveDuplicates());

                int afterCount = _db.GetTotalCount();
                int removedCount = beforeCount - afterCount;

                _totalCount = afterCount;
                ClearPageCache();
                await LoadPageDataAsync();
                UpdateRecordCount();

                MessageBox.Show(
                    $"删除完成，共删除 {removedCount} 条重复记录",
                    "完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                btnDeduplicate.Enabled = true;
                btnDeduplicate.Text = "一键去重";
            }
        }
        #endregion

        #region DataGridView 事件
        private void DataGridView1_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _filteredData.Count)
            {
                e.Value = "";
                return;
            }

            var row = _filteredData[e.RowIndex];
            string colName = dataGridView1.Columns[e.ColumnIndex].Name;

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
                case "CIDR":
                    e.Value = row.Length > 1 ? row[1] : "";
                    break;
                case "Remark":
                    e.Value = row.Length > 2 ? row[2] : "";
                    break;
                default:
                    e.Value = "";
                    break;
            }
        }

        private void DataGridView1_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0)
                return;

            if (e.ColumnIndex < 0)
            {
                if (e.RowIndex < _filteredData.Count)
                {
                    var row = _filteredData[e.RowIndex];
                    if (row.Length > 1)
                    {
                        string cidr = row[1];
                        if (!string.IsNullOrEmpty(cidr))
                        {
                            Clipboard.SetText(cidr);
                            MessageBox.Show(
                                $"已复制：{cidr}",
                                "复制成功",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information
                            );
                        }
                    }
                }
                return;
            }

            string colName = dataGridView1.Columns[e.ColumnIndex].Name;
            if (colName == "CIDR" && e.RowIndex < _filteredData.Count)
            {
                var row = _filteredData[e.RowIndex];
                if (row.Length > 1)
                {
                    string cidr = row[1];
                    string randomIp = GenerateRandomIpFromCidr(cidr);
                    if (!string.IsNullOrEmpty(randomIp))
                    {
                        Clipboard.SetText(randomIp);
                        MessageBox.Show(
                            $"已随机生成 IP: {randomIp}",
                            "生成成功",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information
                        );
                    }
                }
            }
        }

        private void DataGridView1_CellValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0)
                return;

            string colName = dataGridView1.Columns[e.ColumnIndex].Name;
            if (colName != "Remark")
                return;
            if (e.RowIndex >= _filteredData.Count)
                return;

            var row = _filteredData[e.RowIndex];
            if (row.Length < 1)
                return;

            string newRemark = e.Value?.ToString() ?? "";

            if (row.Length < 3)
            {
                Array.Resize(ref row, 3);
            }
            row[2] = newRemark;

            if (int.TryParse(row[0], out int id))
            {
                _db.UpdateRemark(id, newRemark);
            }
        }
        #endregion

        #region 分页和工具方法
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
            btnPrev.Enabled = _currentPage > 1;
            btnNext.Enabled = _currentPage < _totalPages;
        }

        private void UpdateRecordCount()
        {
            int currentPageSize = _filteredData.Count;
            int total = _isSearching ? _searchTotalCount : _totalCount;
            labelPageInfo1.Text = $"第{_currentPage}/{_totalPages}页";
            labelPageInfo2.Text = $"当页{currentPageSize}条/共{total}条";
        }

        private void ComboBox1_SelectedIndexChanged(object? sender, EventArgs e)
        {
            _isOverwriteMode = (comboBoxMode.SelectedIndex == 1);
        }

        private void Cidrs_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _operationCancellationTokenSource?.Cancel();
        }

        private int CalculateDynamicPageSize(int totalCount, int currentPage, int totalPages)
        {
            if (totalCount <= 0)
                return 0;
            if (totalPages <= 1)
                return totalCount;

            int basePageSize = totalCount / totalPages;
            int remainder = totalCount % totalPages;
            int pageSize = currentPage <= remainder ? basePageSize + 1 : basePageSize;

            if (pageSize > DisplayPageSize)
            {
                pageSize = DisplayPageSize;
            }

            return pageSize;
        }

        private int CalculateTotalPages(int totalCount)
        {
            if (totalCount <= 0)
                return 1;
            if (totalCount <= DisplayPageSize)
                return 1;
            return (totalCount + DisplayPageSize - 1) / DisplayPageSize;
        }

        private int GetPageOffset(int pageNumber, int totalCount, int totalPages)
        {
            if (_lastTotalCountForCache != totalCount)
            {
                _pageOffsetCache.Clear();
                _lastTotalCountForCache = totalCount;
            }

            if (_pageOffsetCache.TryGetValue(pageNumber, out int offset))
            {
                return offset;
            }

            offset = 0;
            for (int i = 1; i < pageNumber; i++)
            {
                offset += CalculateDynamicPageSize(totalCount, i, totalPages);
            }

            _pageOffsetCache[pageNumber] = offset;
            return offset;
        }

        // 清空缓存时清除搜索 ID 缓存
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

        // 预取时使用 ID 缓存
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
                        if (_isSearching && _cachedSearchIds != null)
                        {
                            // 使用 ID 缓存快速查询
                            pageData = _db.GetPageDataByIds(_cachedSearchIds, offset, pageSize);
                        }
                        else if (_isSearching)
                        {
                            pageData = _db.SearchPageData(_searchKeyword, offset, pageSize);
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
        #endregion

        #region CIDR 解析工具
        private bool IsValidIPv4(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return false;
            var parts = ip.Split('.');
            if (parts.Length != 4)
                return false;
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out int num))
                    return false;
                if (num < 0 || num > 255)
                    return false;
            }
            return true;
        }

        private bool IsIPv6(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return false;
            return ip.Contains(':');
        }

        private string NormalizeCidr(string cidr)
        {
            cidr = cidr.Trim();
            cidr = Regex.Replace(cidr, @"\s+", " ");

            if (cidr.Contains('/'))
            {
                var parts = cidr.Split('/');
                if (parts.Length != 2)
                    return "";
                string ip = parts[0];
                if (!IsValidIPv4(ip))
                    return "";
                if (!int.TryParse(parts[1], out int mask))
                    return "";
                if (mask < 0 || mask > 32)
                    return "";
                return $"{ip}/{mask}";
            }
            else
            {
                if (!IsValidIPv4(cidr))
                    return "";
                return $"{cidr}/32";
            }
        }

        private List<string> ConvertToMax24(string cidr)
        {
            var result = new List<string>();
            var match = Regex.Match(cidr, @"^(\d+\.\d+\.\d+\.\d+)/(\d+)$");
            if (!match.Success)
                return result;

            string baseIp = match.Groups[1].Value;
            int mask = int.Parse(match.Groups[2].Value);

            if (mask >= 24)
            {
                result.Add(cidr);
                return result;
            }

            var ipParts = baseIp.Split('.').Select(int.Parse).ToArray();
            int hostBits = 32 - mask;
            int subnetCount = (int)Math.Pow(2, hostBits - 8);

            for (int i = 0; i < subnetCount; i++)
            {
                int thirdOctet = ipParts[2] + i;
                if (thirdOctet > 255)
                    break;
                result.Add($"{ipParts[0]}.{ipParts[1]}.{thirdOctet}.0/24");
            }

            return result;
        }

        private List<string> ParseAndConvertCidrData(string content)
        {
            var result = new List<string>();
            var lines = content.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries
            );

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split(
                    new[] { ' ', ',', '\t', ';' },
                    StringSplitOptions.RemoveEmptyEntries
                );
                foreach (var part in parts)
                {
                    if (IsIPv6(part))
                        continue;

                    var normalized = NormalizeCidr(part);
                    if (string.IsNullOrEmpty(normalized))
                        continue;

                    var converted = ConvertToMax24(normalized);
                    result.AddRange(converted);
                }
            }

            return result.Distinct().ToList();
        }

        private string GenerateRandomIpFromCidr(string cidr)
        {
            var match = Regex.Match(cidr, @"^(\d+\.\d+\.\d+\.\d+)/(\d+)$");
            if (!match.Success)
                return "";

            string baseIp = match.Groups[1].Value;
            int mask = int.Parse(match.Groups[2].Value);

            var ipParts = baseIp.Split('.').Select(int.Parse).ToArray();
            int hostBits = 32 - mask;
            int hostCount = (int)Math.Pow(2, hostBits);

            var random = new Random();
            int randomOffset = random.Next(hostCount);
            int lastOctet = ipParts[3] + randomOffset;

            if (lastOctet > 255)
            {
                int carry = lastOctet / 256;
                lastOctet = lastOctet % 256;
                ipParts[2] += carry;
                ipParts[2] = ipParts[2] % 256;
            }

            return $"{ipParts[0]}.{ipParts[1]}.{ipParts[2]}.{lastOctet}";
        }
        #endregion
    }
}
