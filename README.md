
# 一个简单Dapper扩展

这是个对dapper一个简单补充扩展，包含了根据Db类型获取DbParameter命名规则以及一个及其简单的分页扩展支持。


## 通用DbParameter命名规则

通过DbConnectionHelper.OpenDbConnection(connection配置名)打开指定数据库连接后可以通过扩展方法GetPName获取该连接支持的命名参数格式，比如：

    var conn = DbConnectionHelper.OpenDbConnection("DB");
    var P_uid = conn.GetPName("uid");
    var result = conn.Query($"select * from user where uid={P_uid}", new Dictionary<string, object>()
    {
        [P_uid] = "root"
    });

ConnectionString配置为

    <add name="DB" providerName="System.Data.SqlClient" connectionString="xxxxx" />

则P_uid就是SqlServer规则的 "@uid"

如果配置变为

    <add name="DB" providerName="Oracle.ManagedDataAccess.Client" connectionString="xxxxx" />


P_uid就会变成Oracle规则的 ":uid"

这部分功能是通过检索服务器参数绑定信息实现的， 因此适用于任何一个标准的 .net Db Provider 实现。

* **注意**  *DbConnectionHelper.OpenDbConnection(connection配置名)* 会返回一个已打开的数据库连接，需要手工显示关闭！


## 简单分页

简单分页是通过检测数据库连接的版本信息来分别对不同数据库进行分页处理，并且由于是简单处理并不对传入sql进行分析拆解，所以目前只支持3种数据库SqlServer, Oracle, MySql的特定分页语句。

分页方法与dapper query类似：

    IEnumerable<dynamic> conn.Paging(string sql, int pageSize, int pageNum, object param = null, ...) 

或

    IEnumerable<T> conn.Paging<T>(string sql, int pageSize, int pageNum, object param = null, ...)

例子：

    var oconn = DbConnectionHelper.OpenDbConnection("Oracle");
    var result = oconn.Paging("select * from user order by uid", 10, 1);
    result = oconn.Paging("select * from user order by uid", 10, 2);

    var sconn = DbConnectionHelper.OpenDbConnection("SQLServer");
    result = sconn.Paging("select * from user order by uid", 10, 1);
    result = sconn.Paging("select * from user order by uid", 10, 2);

    var mconn = DbConnectionHelper.OpenDbConnection("MySql");
    result = mconn.Paging("select * from user order by uid", 10 1);
    result = mconn.Paging("select * from user order by uid", 10, 2);

### SqlServer

由于SqlServer分页的特殊性，对于SqlServer只支持SqlServer2012版本及以后的OffsetFetch分页方式，如果是用低于此版本的SqlServer，需要自己手动把已有sql语句改造成row_number() over(order by )方式后直接用dapper query查询。对于OffsetFetch方式也有一个小小的限制，就是sql语句必须含有order by

对于SqlServer2012以下版本调用Paging会产生NotSupportedException

### Oracle

对于Oracle支持两种分页方式，rownum和OffsetFetch，当Oracle主版本大于等于12时会采用OffsetFetch，OffsetFetch限制和SqlServer一样sql需要有order by

### MySql

对于MySql只支持了Limit一种分页方式

### Sqlite

对于Sqlite只支持Limit Offset分页方式

### 其他数据库

不支持其他数据库（测试环境只装有3种数据库），如果要支持目前可通过直接修改MeasureDbPagingType，以及添加DbPagingType及DbPagingFunc来支持。