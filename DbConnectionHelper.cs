﻿using Dapper;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using static MyDapperExtension.DbConnectionHelper;
using System.Threading.Tasks;

namespace MyDapperExtension
{
    public static class DbConnectionHelper
    {
        public enum DbPagingType
        {
            None = 0,
            RowNumOracle = 1,
            OffsetFetch = 2,
            Limit = 3,
            Sqlite = 4,
        }

        struct DbSettingItem
        {
            public bool NamedParameterSupport;
            public string ParameterMarker;
            public DbPagingType PagingType;
        }

        public static string DefaultParameterNameFormat = "@{0}";

        private static readonly ConcurrentDictionary<string, DbSettingItem> DbSettings = new ConcurrentDictionary<string, DbSettingItem>();
        private static readonly Dictionary<DbPagingType, Func<string, int, int, string>>
            DbPagingFunc = new Dictionary<DbPagingType, Func<string, int, int, string>>()
            {
                [DbPagingType.RowNumOracle] = PagingMethod_RowNumOracle,
                [DbPagingType.OffsetFetch] = PagingMethod_OffsetFetch,
                [DbPagingType.Limit] = PagingMethod_Limit,
                [DbPagingType.Sqlite] = PagingMethod_Sqlite,
            };

        private static readonly Regex regex = new Regex(@"order\s+by", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string PagingMethod_RowNumOracle(string sql, int pageSize, int pageNum)
        {
            int upper = pageSize * pageNum + 1;
            int lower = (pageNum - 1) * pageSize + 1;

            return "SELECT * FROM " +
            " (" +
            " SELECT A.*, rownum r__ " +
            " FROM " +
            " ( " +
            sql +
            " ) A " +
            $" WHERE rownum < {upper} " +
        " ) B " +
            $" WHERE r__ >= {lower}";
        }

        private static string PagingMethod_OffsetFetch(string sql, int pageSize, int pageNum)
        {
            if (!regex.IsMatch(sql))
                throw new NotSupportedException("OffsetFetch paging must contain \"order by\"");
            int offset = (pageNum - 1) * pageSize;
            return sql + $" OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";
        }

        private static string PagingMethod_Limit(string sql, int pageSize, int pageNum)
        {
            int offset = (pageNum - 1) * pageSize;
            return sql + $" limit {offset},{pageSize}";
        }

        private static string PagingMethod_Sqlite(string sql, int pageSize, int pageNum)
        {
            int offset = (pageNum - 1) * pageSize;
            return sql + $" limit {pageSize} offset {offset}";
        }

        private static DbPagingType MeasureDbPagingType(string product, string version)
        {
            product = product.ToLower();
            version = version.Trim();
            var major = 0;
            try
            {
                major = int.Parse(version.Split('.')[0]);
            }
            catch { }

            if (product.Contains("oracle"))
            {
                if (major >= 12)
                {
                    return DbPagingType.OffsetFetch;
                }

                return DbPagingType.RowNumOracle;
            }

            if (product.Contains("sql server"))
            {
                if (major >= 12)
                {
                    return DbPagingType.OffsetFetch;
                }

                return DbPagingType.None;
            }

            if (product.Contains("mysql"))
            {
                return DbPagingType.Limit;
            }

            if (product.Contains("sqlite"))
            {
                return DbPagingType.Sqlite;
            }

            return DbPagingType.None;
        }

        private static DbSettingItem MeasureDbSetting(DataTable schema)
        {
            var pattern = schema.Rows[0][DbMetaDataColumnNames.ParameterMarkerPattern] as string;
            var len = (int)schema.Rows[0][DbMetaDataColumnNames.ParameterNameMaxLength];
            pattern = pattern.TrimStart(' ', '(', '\\');

            var product = schema.Rows[0][DbMetaDataColumnNames.DataSourceProductName] as string;
            var version = schema.Rows[0][DbMetaDataColumnNames.DataSourceProductVersion] as string;
            var pagingType = MeasureDbPagingType(product, version);

            return new DbSettingItem
            {
                ParameterMarker = $"{pattern.FirstOrDefault()}",
                NamedParameterSupport = len != 0,
                PagingType = pagingType
            };
        }

        private static DbSettingItem MeasureDbSetting(this DbConnection connection)
        {
            var schema = connection.GetSchema(DbMetaDataCollectionNames.DataSourceInformation);
            return MeasureDbSetting(schema);
        }

        private static bool IsNeedMeasureDbSetting(this DbConnection connection, string providerName)
        {
            if (DbSettings.ContainsKey(connection.ConnectionString))
                return false;

            if (providerName.ToLower().Contains("sqlite"))
            {
                DbSettings[connection.ConnectionString] = new DbSettingItem
                {
                    ParameterMarker = DefaultParameterNameFormat,
                    NamedParameterSupport = true,
                    PagingType = DbPagingType.Sqlite
                };
                return false;
            }
            return true;
        }

        private static void InitConnectionParameterMarker(this DbConnection connection, string providerName)
        {
            if (!connection.IsNeedMeasureDbSetting(providerName))
                return;

            DbSettings[connection.ConnectionString] = connection.MeasureDbSetting();
        }

        public static DbConnection OpenDbConnection(string providerName, string connectionString)
        {
            var factory = DbProviderFactories.GetFactory(providerName);
            var connection = factory.CreateConnection() ?? throw new ArgumentException($"{providerName} create connection failed", nameof(providerName));
            connection.ConnectionString = connectionString;
            connection.Open();
            connection.InitConnectionParameterMarker(providerName);
            return connection;
        }

        public static async Task<DbConnection> OpenDbConnectionAsync(string providerName, string connectionString)
        {
            var factory = DbProviderFactories.GetFactory(providerName);
            var connection = factory.CreateConnection() ?? throw new ArgumentException($"{providerName} create connection failed", nameof(providerName));
            connection.ConnectionString = connectionString;
            await connection.OpenAsync();
            connection.InitConnectionParameterMarker(providerName);
            return connection;
        }

        public static async Task<DbConnection> OpenDbConnectionAsync(string configName)
        {
            return await OpenDbConnectionAsync(ConfigurationManager.ConnectionStrings[configName].ProviderName, ConfigurationManager.ConnectionStrings[configName].ConnectionString);
        }

        public static DbConnection OpenDbConnection(string configName)
        {
            return OpenDbConnection(ConfigurationManager.ConnectionStrings[configName].ProviderName, ConfigurationManager.ConnectionStrings[configName].ConnectionString);
        }

        public static string GetPName(this DbConnection connection, string parameterName)
        {
            if (!DbSettings.TryGetValue(connection.ConnectionString, out DbSettingItem item))
                return string.Format(DefaultParameterNameFormat, parameterName);
            return item.NamedParameterSupport ? $"{item.ParameterMarker}{parameterName}" : $"{item.ParameterMarker}";
        }

        private static DbPagingType GetPagingType(this DbConnection connection)
        {
            if (!DbSettings.TryGetValue(connection.ConnectionString, out DbSettingItem item))
                return DbPagingType.None;
            return item.PagingType;
        }

        private static string GetPagingSql(this DbConnection connection, string sql, int pageSize, int pageNum)
        {
            var pType = connection.GetPagingType();
            if (pType == DbPagingType.None)
                throw new NotSupportedException();

            return DbPagingFunc[pType](sql, pageSize, pageNum);
        }

        public static IEnumerable<T> Paging<T>(this DbConnection connection, string sql, int pageSize, int pageNum, object param = null, IDbTransaction transaction = null, bool buffered = true, int? commandTimeout = null, CommandType? commandType = null)
        {
            return connection.Query<T>(connection.GetPagingSql(sql, pageSize, pageNum), param, transaction, buffered, commandTimeout, commandType);
        }

        public static IEnumerable<dynamic> Paging(this DbConnection connection, string sql, int pageSize, int pageNum, object param = null, IDbTransaction transaction = null, bool buffered = true, int? commandTimeout = null, CommandType? commandType = null)
        {
            return connection.Query(connection.GetPagingSql(sql, pageSize, pageNum), param, transaction, buffered, commandTimeout, commandType);
        }

        public static async Task<IEnumerable<T>> PagingAsync<T>(this DbConnection connection, string sql, int pageSize, int pageNum, object param = null, IDbTransaction transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        {
            return await connection.QueryAsync<T>(connection.GetPagingSql(sql, pageSize, pageNum), param, transaction, commandTimeout, commandType);
        }

        public static async Task<IEnumerable<dynamic>> PagingAsync(this DbConnection connection, string sql, int pageSize, int pageNum, object param = null, IDbTransaction transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        {
            return await connection.QueryAsync(connection.GetPagingSql(sql, pageSize, pageNum), param, transaction, commandTimeout, commandType);
        }
    }
}
