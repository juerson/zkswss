using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace ZKSWorkerGUI
{
    public class DatabaseHelper
    {
        #region 单例模式
        private readonly string _connectionString;
        private static DatabaseHelper? _instance;
        private static readonly object _lock = new object();

        public static DatabaseHelper Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            string dbPath = System.IO.Path.Combine(
                                AppDomain.CurrentDomain.BaseDirectory,
                                "data",
                                "proxyip.db"
                            );
                            _instance = new DatabaseHelper(dbPath);
                        }
                    }
                }
                return _instance;
            }
        }
        #endregion

        #region 构造函数和初始化
        public DatabaseHelper(string dbPath)
        {
            var directory = System.IO.Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
            _connectionString = $"Data Source={dbPath}";
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // 性能优化配置
            var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText =
                @"
                PRAGMA journal_mode = WAL; 
                PRAGMA synchronous = NORMAL;
                PRAGMA cache_size = -200000;
                PRAGMA temp_store = MEMORY;
                PRAGMA mmap_size = 536870912;
                PRAGMA busy_timeout = 5000;
            ";
            pragmaCmd.ExecuteNonQuery();

            var command = connection.CreateCommand();
            command.CommandText =
                @"
                CREATE TABLE IF NOT EXISTS ProxyIP (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    IP TEXT NOT NULL,
                    Port TEXT NOT NULL,
                    Country TEXT,
                    Organization TEXT
                );
                
                CREATE INDEX IF NOT EXISTS idx_ip ON ProxyIP(IP);
                CREATE INDEX IF NOT EXISTS idx_country ON ProxyIP(Country);
                CREATE INDEX IF NOT EXISTS idx_org ON ProxyIP(Organization);
                CREATE INDEX IF NOT EXISTS idx_port ON ProxyIP(Port);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_ip_port ON ProxyIP(IP, Port);
                CREATE INDEX IF NOT EXISTS idx_id_order ON ProxyIP(Id);
            ";
            command.ExecuteNonQuery();

            command.CommandText = "ANALYZE;";
            command.ExecuteNonQuery();
        }
        #endregion

        #region 查询数据
        public int GetTotalCount()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM ProxyIP";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public List<string[]> GetPageData(int startIndex, int count)
        {
            var results = new List<string[]>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
                @"
                SELECT IP, Port, Country, Organization 
                FROM ProxyIP 
                ORDER BY Id 
                LIMIT @count OFFSET @offset
            ";
            command.Parameters.AddWithValue("@offset", startIndex);
            command.Parameters.AddWithValue("@count", count);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(
                    new string[]
                    {
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? " " : reader.GetString(2),
                        reader.IsDBNull(3) ? " " : reader.GetString(3),
                    }
                );
            }

            return results;
        }

        private static (string whereClause, string searchParam) BuildSearchWhereClause(
            string searchText,
            string column,
            string matchMode
        )
        {
            if (string.IsNullOrEmpty(searchText))
            {
                return (string.Empty, string.Empty);
            }

            string whereClause;
            if (column == "全部")
            {
                if (matchMode == "精准匹配")
                {
                    whereClause =
                        "WHERE IP = @search OR Port = @search OR Country = @search OR Organization = @search";
                    return (whereClause, searchText);
                }

                whereClause =
                    "WHERE IP LIKE @search OR Port LIKE @search OR Country LIKE @search OR Organization LIKE @search";
            }
            else
            {
                if (matchMode == "精准匹配")
                {
                    whereClause = $"WHERE {column} = @search";
                    return (whereClause, searchText);
                }

                whereClause = $"WHERE {column} LIKE @search";
            }

            return (whereClause, $"%{searchText}%");
        }

        public int GetSearchCount(string searchText, string column, string matchMode)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            var (whereClause, searchParam) = BuildSearchWhereClause(searchText, column, matchMode);

            command.CommandText =
                $@"
                SELECT COUNT(*) 
                FROM ProxyIP 
                {whereClause}
            ";

            if (!string.IsNullOrEmpty(whereClause))
            {
                command.Parameters.AddWithValue("@search", searchParam);
            }

            return Convert.ToInt32(command.ExecuteScalar());
        }

        public List<string[]> SearchPageData(
            string searchText,
            string column,
            string matchMode,
            int offset,
            int count
        )
        {
            var results = new List<string[]>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var (whereClause, searchParam) = BuildSearchWhereClause(searchText, column, matchMode);

            var command = connection.CreateCommand();
            command.CommandText =
                $@"
                SELECT IP, Port, Country, Organization 
                FROM ProxyIP 
                {whereClause}
                ORDER BY Id
                LIMIT @count OFFSET @offset
            ";

            command.Parameters.AddWithValue("@offset", offset);
            command.Parameters.AddWithValue("@count", count);

            if (!string.IsNullOrEmpty(whereClause))
            {
                command.Parameters.AddWithValue("@search", searchParam);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(
                    new string[]
                    {
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? " " : reader.GetString(2),
                        reader.IsDBNull(3) ? " " : reader.GetString(3),
                    }
                );
            }

            return results;
        }

        #region 搜索 ID 缓存
        public List<long> GetSearchIds(string searchText, string column, string matchMode)
        {
            var ids = new List<long>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var (whereClause, searchParam) = BuildSearchWhereClause(searchText, column, matchMode);

            var command = connection.CreateCommand();
            command.CommandText =
                $@"
                SELECT Id 
                FROM ProxyIP 
                {whereClause}
                ORDER BY Id
            ";

            if (!string.IsNullOrEmpty(whereClause))
            {
                command.Parameters.AddWithValue("@search", searchParam);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }

            return ids;
        }

        public List<string[]> GetPageDataByIds(List<long> ids, int offset, int count)
        {
            var results = new List<string[]>();

            if (ids == null || ids.Count == 0)
                return results;

            int endOffset = Math.Min(offset + count, ids.Count);
            if (offset >= ids.Count)
                return results;

            var pageIds = ids.Skip(offset).Take(endOffset - offset).ToList();
            if (pageIds.Count == 0)
                return results;

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var idsStr = string.Join(", ", pageIds);
            var command = connection.CreateCommand();
            command.CommandText =
                $@"
                SELECT IP, Port, Country, Organization 
                FROM ProxyIP 
                WHERE Id IN ({idsStr})
                ORDER BY Id
            ";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(
                    new string[]
                    {
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? " " : reader.GetString(2),
                        reader.IsDBNull(3) ? " " : reader.GetString(3),
                    }
                );
            }

            return results;
        }

        public void ClearSearchCache()
        {
            // 外部管理缓存
        }
        #endregion
        #endregion

        #region 删除数据
        public void ClearAll()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ProxyIP";
            command.ExecuteNonQuery();
        }

        public void DeleteByIpPortList(List<(string ip, string port)> items)
        {
            if (items == null || items.Count == 0)
                return;

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();

            var idsToDelete = new List<long>();
            var queryCommand = connection.CreateCommand();
            queryCommand.CommandText = "SELECT Id FROM ProxyIP WHERE IP = @ip AND Port = @port";
            var ipParam = queryCommand.Parameters.Add("@ip", SqliteType.Text);
            var portParam = queryCommand.Parameters.Add("@port", SqliteType.Text);

            foreach (var item in items)
            {
                ipParam.Value = item.ip;
                portParam.Value = item.port;
                var result = queryCommand.ExecuteScalar();
                if (result != null)
                {
                    idsToDelete.Add((long)result);
                }
            }

            if (idsToDelete.Count > 0)
            {
                var deleteCommand = connection.CreateCommand();
                var idsStr = string.Join(", ", idsToDelete);
                deleteCommand.CommandText = $"DELETE FROM ProxyIP WHERE Id IN ({idsStr})";
                deleteCommand.ExecuteNonQuery();
            }

            transaction.Commit();

            var reindexCommand = connection.CreateCommand();
            reindexCommand.CommandText = "REINDEX";
            reindexCommand.ExecuteNonQuery();
        }
        #endregion

        #region 插入数据
        public void InsertOrReplaceData(List<string[]> dataList)
        {
            if (dataList == null || dataList.Count == 0)
                return;

            const int batchSize = 1000;
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();

            var command = connection.CreateCommand();
            command.CommandText =
                @"
                INSERT OR REPLACE INTO ProxyIP (IP, Port, Country, Organization) 
                VALUES (@ip, @port, @country, @org)
            ";
            var ipParam = command.Parameters.Add("@ip", SqliteType.Text);
            var portParam = command.Parameters.Add("@port", SqliteType.Text);
            var countryParam = command.Parameters.Add("@country", SqliteType.Text);
            var orgParam = command.Parameters.Add("@org", SqliteType.Text);

            for (int i = 0; i < dataList.Count; i += batchSize)
            {
                int endIndex = Math.Min(i + batchSize, dataList.Count);
                for (int j = i; j < endIndex; j++)
                {
                    var row = dataList[j];
                    ipParam.Value = row[0];
                    portParam.Value = row[1];
                    countryParam.Value = row.Length > 2 ? row[2] : " ";
                    orgParam.Value = row.Length > 3 ? row[3] : " ";
                    command.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        public void InsertOrIgnoreData(List<string[]> dataList)
        {
            if (dataList == null || dataList.Count == 0)
                return;

            const int batchSize = 1000;
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();

            var insertCmd = connection.CreateCommand();
            insertCmd.CommandText =
                @"
                INSERT OR IGNORE INTO ProxyIP (IP, Port, Country, Organization) 
                VALUES (@ip, @port, @country, @org)
            ";
            var ipParam = insertCmd.Parameters.Add("@ip", SqliteType.Text);
            var portParam = insertCmd.Parameters.Add("@port", SqliteType.Text);
            var countryParam = insertCmd.Parameters.Add("@country", SqliteType.Text);
            var orgParam = insertCmd.Parameters.Add("@org", SqliteType.Text);

            for (int i = 0; i < dataList.Count; i += batchSize)
            {
                int endIndex = Math.Min(i + batchSize, dataList.Count);
                for (int j = i; j < endIndex; j++)
                {
                    var row = dataList[j];
                    ipParam.Value = row[0];
                    portParam.Value = row[1];
                    countryParam.Value = row.Length > 2 ? row[2] : " ";
                    orgParam.Value = row.Length > 3 ? row[3] : " ";
                    insertCmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
        #endregion

        #region 维护数据
        public void RemoveDuplicates()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
                @"
                DELETE FROM ProxyIP 
                WHERE Id NOT IN (
                    SELECT MIN(Id) 
                    FROM ProxyIP 
                    GROUP BY IP, Port
                )
            ";
            command.ExecuteNonQuery();
        }
        #endregion
    }

    #region CIDR 独立数据库
    public class CidrDatabaseHelper
    {
        private readonly string _connectionString;
        private static CidrDatabaseHelper? _instance;
        private static readonly object _lock = new object();

        public static CidrDatabaseHelper Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            string dbPath = System.IO.Path.Combine(
                                AppDomain.CurrentDomain.BaseDirectory,
                                "data",
                                "cidrs.db"
                            );
                            _instance = new CidrDatabaseHelper(dbPath);
                        }
                    }
                }
                return _instance;
            }
        }

        public CidrDatabaseHelper(string dbPath)
        {
            var directory = System.IO.Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
            _connectionString = $"Data Source={dbPath}";
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // 性能优化配置
            var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText =
                @"
                PRAGMA journal_mode = WAL; 
                PRAGMA synchronous = NORMAL;
                PRAGMA cache_size = -200000;
                PRAGMA temp_store = MEMORY;
                PRAGMA mmap_size = 536870912;
                PRAGMA busy_timeout = 5000;
            ";
            pragmaCmd.ExecuteNonQuery();

            var command = connection.CreateCommand();
            command.CommandText =
                @"
                CREATE TABLE IF NOT EXISTS CIDRs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CIDR TEXT NOT NULL UNIQUE,
                    Remark TEXT
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_cidr ON CIDRs(CIDR);
                CREATE INDEX IF NOT EXISTS idx_remark ON CIDRs(Remark);
                CREATE INDEX IF NOT EXISTS idx_search ON CIDRs(CIDR, Remark);
            ";
            command.ExecuteNonQuery();

            command.CommandText = "ANALYZE;";
            command.ExecuteNonQuery();
        }

        public int GetTotalCount()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM CIDRs";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public List<string[]> GetPageData(int startIndex, int count)
        {
            var results = new List<string[]>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
                @"
                SELECT Id, CIDR, Remark,
                    CAST(SUBSTR(CIDR, 1, INSTR(CIDR, '.') - 1) AS INTEGER) as p1,
                    CAST(SUBSTR(CIDR, INSTR(CIDR, '.') + 1, INSTR(SUBSTR(CIDR, INSTR(CIDR, '.') + 1), '.') - 1) AS INTEGER) as p2
                FROM CIDRs 
                ORDER BY p1, p2
                LIMIT @count OFFSET @offset
            ";
            command.Parameters.AddWithValue("@offset", startIndex);
            command.Parameters.AddWithValue("@count", count);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(
                    new string[]
                    {
                        reader.GetInt32(0).ToString(),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? " " : reader.GetString(2),
                    }
                );
            }

            return results;
        }

        public void InsertOrIgnoreCidrs(List<string[]> cidrList)
        {
            if (cidrList == null || cidrList.Count == 0)
                return;

            var sortedList = cidrList
                .OrderBy(x => GetIpNumericValue(x[0]))
                .ThenBy(x => GetMaskValue(x[0]))
                .ToList();

            const int batchSize = 1000;
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();

            var command = connection.CreateCommand();
            command.CommandText =
                @"
                INSERT OR IGNORE INTO CIDRs (CIDR, Remark) 
                VALUES (@cidr, @remark)
            ";
            var cidrParam = command.Parameters.Add("@cidr", SqliteType.Text);
            var remarkParam = command.Parameters.Add("@remark", SqliteType.Text);

            for (int i = 0; i < sortedList.Count; i += batchSize)
            {
                int endIndex = Math.Min(i + batchSize, sortedList.Count);
                for (int j = i; j < endIndex; j++)
                {
                    var row = sortedList[j];
                    cidrParam.Value = row[0];
                    remarkParam.Value = row.Length > 1 ? row[1] : " ";
                    command.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        private static long GetIpNumericValue(string cidr)
        {
            try
            {
                var parts = cidr.Split('/')[0].Split('.');
                if (parts.Length != 4)
                    return long.MaxValue;
                long ip = 0;
                ip = ip * 256 + long.Parse(parts[0]);
                ip = ip * 256 + long.Parse(parts[1]);
                ip = ip * 256 + long.Parse(parts[2]);
                ip = ip * 256 + long.Parse(parts[3]);
                return ip;
            }
            catch
            {
                return long.MaxValue;
            }
        }

        private static int GetMaskValue(string cidr)
        {
            try
            {
                var parts = cidr.Split('/');
                return parts.Length > 1 ? int.Parse(parts[1]) : 32;
            }
            catch
            {
                return 32;
            }
        }

        public void ClearAll()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CIDRs";
            command.ExecuteNonQuery();
        }

        public void DeleteByIdList(List<int> ids)
        {
            if (ids == null || ids.Count == 0)
                return;

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var idsStr = string.Join(", ", ids);
            var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM CIDRs WHERE Id IN ({idsStr})";
            command.ExecuteNonQuery();
        }

        public void RemoveDuplicates()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
                @"
                DELETE FROM CIDRs 
                WHERE Id NOT IN (
                    SELECT MIN(Id) 
                    FROM CIDRs 
                    GROUP BY CIDR
                )
            ";
            command.ExecuteNonQuery();
        }

        public void UpdateRemark(int id, string remark)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "UPDATE CIDRs SET Remark = @remark WHERE Id = @id";
            command.Parameters.AddWithValue("@remark", remark);
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }

        public int GetSearchCount(string keyword)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
                @"
                SELECT COUNT(*) FROM CIDRs 
                WHERE CIDR LIKE @keyword OR Remark LIKE @keyword
            ";
            command.Parameters.AddWithValue("@keyword", $"%{keyword}%");
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public List<string[]> SearchPageData(string keyword, int startIndex, int count)
        {
            var results = new List<string[]>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
                @"
                SELECT Id, CIDR, Remark,
                    CAST(SUBSTR(CIDR, 1, INSTR(CIDR, '.') - 1) AS INTEGER) as p1,
                    CAST(SUBSTR(CIDR, INSTR(CIDR, '.') + 1, INSTR(SUBSTR(CIDR, INSTR(CIDR, '.') + 1), '.') - 1) AS INTEGER) as p2
                FROM CIDRs 
                WHERE CIDR LIKE @keyword OR Remark LIKE @keyword
                ORDER BY p1, p2
                LIMIT @count OFFSET @offset
            ";
            command.Parameters.AddWithValue("@keyword", $"%{keyword}%");
            command.Parameters.AddWithValue("@offset", startIndex);
            command.Parameters.AddWithValue("@count", count);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(
                    new string[]
                    {
                        reader.GetInt32(0).ToString(),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? " " : reader.GetString(2),
                    }
                );
            }

            return results;
        }

        #region 搜索 ID 缓存
        public List<long> GetSearchIds(string keyword)
        {
            var ids = new List<long>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
                @"
                SELECT Id 
                FROM CIDRs 
                WHERE CIDR LIKE @keyword OR Remark LIKE @keyword
                ORDER BY Id
            ";
            command.Parameters.AddWithValue("@keyword", $"%{keyword}%");

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }

            return ids;
        }

        public List<string[]> GetPageDataByIds(List<long> ids, int offset, int count)
        {
            var results = new List<string[]>();

            if (ids == null || ids.Count == 0)
                return results;

            if (offset >= ids.Count)
                return results;

            // ✅ 直接计算索引范围，避免 Skip 遍历
            int startIndex = offset;
            int endIndex = Math.Min(offset + count, ids.Count);
            int pageSize = endIndex - startIndex;

            if (pageSize <= 0)
                return results;

            // ✅ 使用 GetRange 替代 Skip+Take
            var pageIds = ids.GetRange(startIndex, pageSize);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // ✅ 使用参数化查询，避免 SQL 拼接
            var command = connection.CreateCommand();
            var idParams = new List<string>();
            for (int i = 0; i < pageIds.Count; i++)
            {
                string paramName = $"@id{i}";
                idParams.Add(paramName);
                command.Parameters.AddWithValue(paramName, pageIds[i]);
            }

            command.CommandText =
                $@"
                SELECT Id, CIDR, Remark 
                FROM CIDRs 
                WHERE Id IN ({string.Join(", ", idParams)})
                ORDER BY Id
            ";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(
                    new string[]
                    {
                        reader.GetInt32(0).ToString(),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? "  " : reader.GetString(2),
                    }
                );
            }

            return results;
        }

        public void ClearSearchCache()
        {
            // 外部管理缓存
        }
        #endregion
    }
    #endregion
}
