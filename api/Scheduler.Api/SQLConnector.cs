using Microsoft.Data.SqlClient;
using System.Data;
using System.Dynamic;

namespace Scheduler.Api
{
    internal class SQLConnector : IDisposable
    {
        public static SQLConnector GetConnection(string conn)
        {
            return new SQLConnector(conn, true);
        }
        /// <summary>
        /// Returns the state of the current connection
        /// </summary>
        public ConnectionState State
        {
            get
            {
                if (Conn == null)
                    return ConnectionState.Closed;
                return Conn.State;
            }
        }
        private string PrevConnString { get; set; }
        /// <summary>
        /// Internal SQLConnection object
        /// </summary>
        private SqlConnection? Conn { get; set; }
        /// <summary>
        /// Change the max pool size, default to 100
        /// </summary>
        public int MaxPoolSize { get; set; }
        /// <summary>
        /// Property to increase the default SQL Statement execution timeout period of 30 seconds.
        /// </summary>
        public int TimeOut { get; set; }

        /// <summary>
        /// Cleans the curren connection if it exists.
        /// </summary>
        private void CleanConnection()
        {
            if (Conn != null)
            {
                if (Conn.State != System.Data.ConnectionState.Closed)
                    Conn.Close();
                Conn.Dispose();
            }
            Conn = null;
        }
        public void Connect()
        {
            ConnectToDB(PrevConnString);
        }
        /// <summary>
        /// Connects to a database using the given connection string
        /// </summary>
        /// <param name="ConnectionString">The Connection string for the database</param>
        private void ConnectToDB(string ConnectionString)
        {
            PrevConnString = ConnectionString;
            CleanConnection();
            try
            {
                Conn = new SqlConnection(ConnectionString);
                Conn.Open();
            }
            catch (Exception ex)
            {
                // Failed to onnect, ohh well, send the error to the calling app.
                throw new Exception(ex.Message, ex);
            }
        }
        /// <summary>
        /// Create a connection to the database using standard SQL authenication.
        /// </summary>
        /// <param name="Server">Server name</param>
        /// <param name="Database">Database</param>
        /// <param name="Username">Username</param>
        /// <param name="Password">Password</param>
        /// <param name="ConnectInstantly">Should an automatic connection be established?</param>
        public SQLConnector(string Server, string Database, string Username, string Password, bool ConnectInstantly)
        {
            MaxPoolSize = 100;
            TimeOut = 30000;
            int timeout = TimeOut / 1000;
            if (Server == null || Database == null || Server.Trim() == "" || Database.Trim() == "")
            {
                throw new Exception("No valid server / database was specified");
            }
            PrevConnString = "Data Source=" + Server + ";Initial Catalog=" + Database + ";" + (Username == null || Username.Trim() == "" ? "" : " User ID=" + Username + ";") + (Password == null || Password.Trim() == "" ? "" : " Password=" + Password + ";") + " Max Pool Size=" + MaxPoolSize.ToString() + "; TrustServerCertificate=True; Connect Timeout=" + timeout.ToString();
            if (ConnectInstantly)
                ConnectToDB(PrevConnString);
        }
        /// <summary>
        /// Create a connection to the database using standard SQL authenication.
        /// </summary>
        /// <param name="Server">Server name</param>
        /// <param name="Database">Database</param>
        /// <param name="Username">Username</param>
        /// <param name="Password">Password</param>
        public SQLConnector(string Server, string Database, string Username, string Password)
        {
            MaxPoolSize = 100;
            TimeOut = 30000;
            int timeout = TimeOut / 1000;
            if (Server == null || Database == null || Server.Trim() == "" || Database.Trim() == "")
            {
                throw new Exception("No valid server / database was specified");
            }
            PrevConnString = "Data Source=" + Server + ";Initial Catalog=" + Database + ";" + (Username == null || Username.Trim() == "" ? "" : " User ID=" + Username + ";") + (Password == null || Password.Trim() == "" ? "" : " Password=" + Password + ";") + " Max Pool Size=" + MaxPoolSize.ToString() + "; TrustServerCertificate=True; Connect Timeout=" + timeout.ToString();
            ConnectToDB(PrevConnString);
        }
        /// <summary>
        /// Create a connection to the database using a connection string, but also allows you to defer connection to a manual connection
        /// </summary>
        /// <param name="ConnectionString">Connection String</param>
        /// <param name="ConnectInstantly">Should an automatic connection be established?</param>
        public SQLConnector(string ConnectionString, bool ConnectInstantly)
        {
            MaxPoolSize = 100;
            TimeOut = 30000;
            PrevConnString = ConnectionString + "TrustServerCertificate=True;";
            if (ConnectInstantly)
                ConnectToDB(ConnectionString);
        }
        /// <summary>
        /// Disposer, so we can use withing a using statement.
        /// </summary>
        public void Dispose()
        {
            CleanConnection();
            // This object will be cleaned up by the Dispose method. 
            // Therefore, you should call GC.SupressFinalize to 
            // take this object off the finalization queue  
            // and prevent finalization code for this object 
            // from executing a second time.
            GC.SuppressFinalize(this);
        }
        /// <summary>
        /// Executes an SQL statement with no results
        /// </summary>
        /// <param name="strSQL">SQL Statement</param>
        public void ExecuteNRSQL(string strSQL)
        {
            if (Conn == null)
                return;
            using (SqlCommand cmdCommand = Conn.CreateCommand())
            {
                cmdCommand.CommandText = strSQL;
                cmdCommand.CommandTimeout = TimeOut;
                cmdCommand.ExecuteNonQuery();
            }
        }
        /// <summary>
        /// Executes an SQL statement, returns a datatable object
        /// </summary>
        /// <param name="strSQL">SQL Statement</param>
        /// <returns>DataTable results from the SQL Statement</returns>
        public DataTable? ExecuteSQL(string strSQL)
        {
            DataTable? Response = null;
            if (Conn != null)
                using (SqlCommand Command = Conn.CreateCommand())
                {
                    Command.CommandText = strSQL;
                    Command.CommandTimeout = TimeOut;
                    using (SqlDataReader Reader = Command.ExecuteReader())
                    {
                        Response = new DataTable();
                        Response.Load(Reader);
                    }
                }
            return Response;
        }
        /// <summary>
        /// Returns an easily serialised object dataset from a stored procedure.
        /// </summary>
        /// <param name="StoredProcName">Name of the stored procedure</param>
        /// <param name="ReturnValue">Result from the stored procedure</param>
        /// <param name="Args">List of arguments</param>
        /// <returns>Returns an object collection</returns>
        public object? GetObjectSetFromSp(string StoredProcName, ref int ReturnValue, params object[] Args)
        {
            DataTableCollection? dt = GetDataSetFromSP(StoredProcName, ref ReturnValue, Args);
            return GetRt(ref dt);
        }
        /// <summary>
        /// Returns an easily serialised object dataset from a stored procedure.
        /// </summary>
        /// <param name="StoredProcName">Name of the stored procedure</param>
        /// <param name="Args">List of arguments</param>
        /// <returns>Returns an object collection</returns>
        public object? GetObjectSetFromSp(string StoredProcName, params object[] Args)
        {
            int ReturnValue = 0;
            return GetObjectSetFromSp(StoredProcName, ref ReturnValue, Args);
        }
        /// <summary>
        /// Creates a collection of objects from a datatable collection.
        /// </summary>
        /// <param name="dt"></param>
        /// <returns></returns>
        public object? GetRt(ref DataTableCollection? dt)
        {
            List<List<object>>? rt = null;
            List<object>? rt2 = null;
            try
            {
                rt = ConvertToObject(ref dt);
                if (((rt != null)))
                    if (rt.Count == 1)
                        foreach (List<object> item in rt)
                        {
                            rt2 = item;
                            break;
                        }

            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message, ex);
            }
            dt = null;
            if (rt2 != null)
                return rt2;
            return rt;
        }
        /// <summary>
        /// DataTable
        /// </summary>
        /// <param name="dt"></param>
        /// <returns></returns>
        public object GetRt(DataTable dt)
        {
            return ConvertToObject(dt);
        }
        /// <summary>
        /// Execute a stored procedure on the connected server, no results returned
        /// </summary>
        /// <param name="StoredProcName">Name of stored procedure</param>
        /// <param name="ReturnValue">Result from the stored procedure</param>
        /// <param name="Args">List of arguments</param>
        public void ExecuteSP(string StoredProcName, ref int ReturnValue, params object[] Args)
        {
            long lngCounter = 0;
            try
            {
                using (SqlCommand dbCommand = new SqlCommand(StoredProcName, Conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = TimeOut })
                {
                    SqlCommandBuilder.DeriveParameters(dbCommand);
                    lngCounter = 0;
                    // Find cached parameters
                    if ((dbCommand.Parameters.Count > 0))
                        foreach (SqlParameter parameter_loopVariable in dbCommand.Parameters)
                        {
                            SqlParameter parameter = parameter_loopVariable;
                            if (parameter.Direction != ParameterDirection.ReturnValue)
                            {
                                if (Args.Length > lngCounter)
                                    parameter.Value = Args[lngCounter] ?? DBNull.Value;
                                lngCounter = lngCounter + 1;
                            }
                        }
                    // Shove indataSet
                    dbCommand.ExecuteNonQuery();
                    // Retrieve the Result?
                    foreach (SqlParameter parameter_loopVariable in dbCommand.Parameters)
                    {
                        SqlParameter parameter = parameter_loopVariable;
                        if ((parameter.Direction == ParameterDirection.ReturnValue))
                        {
                            ReturnValue = (int)parameter.Value;
                            break;
                        }
                    }
                    // Exit
                }
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message, ex);
            }
        }
        /// <summary>
        /// Execute a stored procedure on the connected server, no results returned
        /// </summary>
        /// <param name="StoredProcName">Name of stored procedure</param>
        /// <param name="Args">List of arguments</param>
        public void ExecuteSP(string StoredProcName, params object[] Args)
        {
            int ReturnValue = 0;
            ExecuteSP(StoredProcName, ref ReturnValue, Args);
        }
        /// <summary>
        /// Executes a stored procedure from the current connection, returning a DataTableCollecion
        /// </summary>
        /// <param name="StoredProcName">Name of stored procedure</param>
        /// <param name="ReturnValue">Result from the stored procedure</param>
        /// <param name="Args">List of arguments</param>
        /// <returns>DataTableCollection of the stored procedure results</returns>
        public DataTableCollection? GetDataSetFromSP(string StoredProcName, ref int ReturnValue, params object?[] Args)
        {
            DataTableCollection? functionReturnValue = null;
            long lngCounter = 0;
            using (SqlCommand dbCommand = new SqlCommand(StoredProcName, Conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = TimeOut })
            {
                try
                {
                    SqlCommandBuilder.DeriveParameters(dbCommand);
                    lngCounter = 0;
                    // Search for previously cached parameters
                    if ((dbCommand.Parameters.Count > 0))
                    {
                        foreach (SqlParameter parameter_loopVariable in dbCommand.Parameters)
                        {
                            SqlParameter parameter = parameter_loopVariable;
                            if (parameter.Direction != ParameterDirection.ReturnValue)
                            {
                                if (Args.Length > lngCounter)
                                    parameter.Value = FixFormat(parameter, Args[lngCounter]);
                                lngCounter = lngCounter + 1;
                            }
                        }
                    }
                    // Execute what we need
                    using (SqlDataAdapter dbData = new SqlDataAdapter(dbCommand))
                    {
                        using (DataSet ds = new DataSet())
                        {
                            // Shove indataSet
                            dbData.Fill(ds);
                            functionReturnValue = ds.Tables;
                        }
                    }
                    // Exit
                }
                catch (Exception ex)
                {
                    throw new Exception(ex.Message, ex);
                }
                try
                {
                    // Retrieve the Result?
                    foreach (SqlParameter parameter_loopVariable in dbCommand.Parameters)
                    {
                        SqlParameter parameter = parameter_loopVariable;
                        if ((parameter.Direction == ParameterDirection.ReturnValue))
                        {
                            ReturnValue = (int)parameter.Value;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return functionReturnValue;
        }
        public object FixFormat(SqlParameter Param, object? Value)
        {
            if (Value == null || Value == DBNull.Value)
                return DBNull.Value;
            if (Param.DbType == DbType.Boolean)
            {
                string Val = Value!.ToString()?.ToUpper() ?? "";
                float fVal = 0;
                if (Val == "TRUE" || Val == "YES")
                    return true;
                if (float.TryParse(Val, out fVal))
                    return fVal == 0 ? false : true;
                return false;
            }
            return Value;
        }
        /// <summary>
        /// Executes a stored procedure from the current connection, returning a DataTableCollecion
        /// </summary>
        /// <param name="StoredProcName">Name of stored procedure</param>
        /// <param name="Args">List of arguments</param>
        /// <returns>DataTableCollection of the stored procedure results</returns>
        public DataTableCollection? GetDataSetFromSP(string StoredProcName, params object[] Args)
        {
            int ReturnValue = 0;
            return GetDataSetFromSP(StoredProcName, ref ReturnValue, Args);
        }
        /// <summary>
        /// Gets a list of the stored procedure parameters, for returning information to clients on the
        /// schema of an SP, placed into a cache for later use.
        /// </summary>
        /// <param name="Procedure">Nam of the procedure</param>
        /// <returns></returns>
        public List<Dictionary<string, object>> GetSPParams(string Procedure)
        {
            List<Dictionary<string, object>> returnItem = new List<Dictionary<string, object>>();
            try
            {
                using (SqlCommand dbCommand = new SqlCommand(Procedure, Conn) { CommandType = CommandType.StoredProcedure })
                {
                    SqlCommandBuilder.DeriveParameters(dbCommand);
                    SqlParameterCollection Params = dbCommand.Parameters;
                    if (Params == null)
                        throw new Exception("Unable to find information on the provided stored procedure");
                    // Convert the list into something that can be returned

                    foreach (SqlParameter item in Params)
                    {
                        if (item.ParameterName.ToLower() != "@return_value")
                        {
                            Dictionary<string, object> header = new Dictionary<string, object>
                        {
                            { "Name", item.ParameterName.ToString() },
                            { "Type", item.DbType.ToString() },
                            { "IsNullable", true },
                            { "Size", item.Size }
                        };
                            returnItem.Add(header);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message, ex);
            }
            return returnItem;
        }
        // ///////////////////////////////////////////////////////////////////////////////////////
        // Converts a datatable item to a serialisable object... we hope.
        private List<List<object>>? ConvertToObject(ref DataTableCollection? dts)
        {
            // Exit on anomolies
            if (dts == null || dts.Count == 0)
                return null;

            List<List<object>> tabs = new List<List<object>> { };
            foreach (DataTable dt in dts)
                tabs.Add(ConvertToObject(dt));
            return tabs;
        }
        // ///////////////////////////////////////////////////////////////////////////////////////
        // Converts a datatable item to a serialisable object... we hope.
        private List<object> ConvertToObject(DataTable dt)
        {
            List<object> rt = new List<object> { };
            List<string> columns = new List<string> { };
            foreach (DataColumn column in dt.Columns)
                columns.Add(column.ColumnName);

            foreach (DataRow row in dt.Rows)
            {
                Dictionary<string, object> header = new Dictionary<string, object>();
                int intColumnCount = columns.Count - 1;
                for (int intCounter = 0; intCounter <= intColumnCount; intCounter++)
                    header.Add(columns[intCounter], row[intCounter]);
                rt.Add(header);
            }
            return rt;
        }
        public static List<dynamic> ToDynamicTable(DataTable table)
        {
            var rows = new List<dynamic>();

            foreach (DataRow row in table.Rows)
            {
                dynamic rowDict = new ExpandoObject();
                foreach (DataColumn col in table.Columns)
                {
                    var dict = (IDictionary<string, object?>)rowDict;
                    if (row[col] == DBNull.Value)
                        dict[col.ColumnName] = null;
                    else
                        dict[col.ColumnName] = row[col];
                }
                rows.Add(rowDict);
            }
            return rows;
        }
        // COnverts to the dynamic object
        public static dynamic ToDynamicObject(DataTableCollection tables)
        {
            List<List<dynamic>> result = new List<List<dynamic>>();

            foreach (DataTable table in tables)
                result.Add(SQLConnector.ToDynamicTable(table));

            return result;
        }
        // ///////////////////////////////////////////////////////////////////////////////////////
        // Remote / asynchronous execution support
        /// <summary>
        /// Creates a connector for a remote server without connecting, so that ConnectAsync can be used.
        /// Integrated security is used when no username is provided.
        /// </summary>
        /// <param name="Server">Server name</param>
        /// <param name="Database">Database</param>
        /// <param name="UserName">Username, null / empty for integrated security</param>
        /// <param name="Password">Password</param>
        /// <returns>A connector that has not yet been connected</returns>
        public static SQLConnector GetRemoteConnection(string Server, string Database, string? UserName, string? Password)
        {
            if (Server == null || Database == null || Server.Trim() == "" || Database.Trim() == "")
                throw new Exception("No valid server / database was specified");
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder();
            builder.DataSource = Server;
            builder.InitialCatalog = Database;
            builder.MaxPoolSize = 100;
            builder.ConnectTimeout = 30;
            builder.ApplicationName = "Dashboards";
            if (UserName == null || UserName.Trim() == "")
                builder.IntegratedSecurity = true;
            else
            {
                builder.UserID = UserName;
                builder.Password = Password ?? "";
            }
            return new SQLConnector(builder.ConnectionString + ";", false);
        }
        /// <summary>
        /// Connects asynchronously using the connection string this connector was created with.
        /// </summary>
        /// <param name="Token">Cancellation token</param>
        public async Task ConnectAsync(CancellationToken Token)
        {
            CleanConnection();
            try
            {
                Conn = new SqlConnection(PrevConnString);
                await Conn.OpenAsync(Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Failed to connect, send the error to the calling app.
                throw new Exception(ex.Message, ex);
            }
        }
        // Executes a stored procedure asynchronously with named parameters, limiting rows per result set and
        // supporting cancellation, used for remote datasource executions.
        /// <summary>
        /// Executes a stored procedure asynchronously using named arguments.
        /// </summary>
        /// <param name="StoredProcName">Name of the stored procedure</param>
        /// <param name="Args">Arguments by parameter name, with or without the @</param>
        /// <param name="MaxRows">Maximum rows read from each result set, null for no limit</param>
        /// <param name="TimeoutSeconds">Command timeout in seconds, null for no limit</param>
        /// <param name="Token">Cancellation token, cancelling it cancels the running command</param>
        /// <returns>DataTableCollection of the stored procedure results</returns>
        public async Task<DataTableCollection?> GetDataSetFromSPAsync(string StoredProcName, Dictionary<string, object?> Args, int? MaxRows, int? TimeoutSeconds, CancellationToken Token)
        {
            if (Conn == null)
                throw new Exception("The connection has not been opened");
            DataSet ds = new DataSet();
            using (SqlCommand dbCommand = new SqlCommand(StoredProcName, Conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = TimeoutSeconds ?? 0 })
            {
                try
                {
                    SqlCommandBuilder.DeriveParameters(dbCommand);
                    // Match the arguments by name
                    Dictionary<string, object?> lookup = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, object?> item in Args)
                        lookup[item.Key.TrimStart('@')] = item.Value;
                    foreach (SqlParameter parameter in dbCommand.Parameters)
                    {
                        if (parameter.Direction == ParameterDirection.ReturnValue)
                            continue;
                        // Every input parameter is sent, anything not supplied (or blank) is NULL
                        if (lookup.TryGetValue(parameter.ParameterName.TrimStart('@'), out object? value))
                            parameter.Value = CoerceParameter(parameter, value);
                        else
                            parameter.Value = DBNull.Value;
                    }
                    // Cancel the running command when the token is cancelled
                    using (Token.Register(() =>
                    {
                        try
                        {
                            dbCommand.Cancel();
                        }
                        catch (Exception)
                        {
                        }
                    }))
                    {
                        // SqlClient cannot skip rows, NextResult / Dispose read and discard whatever the server still sends.
                        // So when a result set has more rows than MaxRows the remote command is cancelled and reading stops,
                        // which means any later result sets are not returned. Executions within the cap return every result set.
                        bool truncated = false;
                        try
                        {
                            using (SqlDataReader reader = await dbCommand.ExecuteReaderAsync(CommandBehavior.Default, Token))
                            {
                            do
                            {
                                DataTable table = new DataTable();
                                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                for (int i = 0; i < reader.FieldCount; i++)
                                {
                                    string name = reader.GetName(i);
                                    if (name == "")
                                        name = "Column" + (i + 1).ToString();
                                    if (!names.Add(name))
                                    {
                                        name = name + "_" + (i + 1).ToString();
                                        names.Add(name);
                                    }
                                    DataColumn column = table.Columns.Add(name, reader.GetFieldType(i) ?? typeof(object));
                                    column.ExtendedProperties["sqlType"] = reader.GetDataTypeName(i);
                                }
                                // Only read up to the maximum rows
                                int rowCount = 0;
                                object[] values = new object[reader.FieldCount];
                                while ((MaxRows == null || MaxRows <= 0 || rowCount < MaxRows) && await reader.ReadAsync(Token))
                                {
                                    reader.GetValues(values);
                                    table.Rows.Add(values);
                                    rowCount++;
                                }
                                ds.Tables.Add(table);
                                // At the cap, one more row tells us whether the result set was really cut short
                                if (MaxRows != null && MaxRows > 0 && rowCount >= MaxRows && await reader.ReadAsync(Token))
                                {
                                    truncated = true;
                                    dbCommand.Cancel();
                                    break;
                                }
                            } while (await reader.NextResultAsync(Token));
                            }
                        }
                        catch (SqlException) when (truncated && !Token.IsCancellationRequested)
                        {
                            // The "operation cancelled by user" error from our own Cancel
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (Token.IsCancellationRequested)
                        throw new OperationCanceledException(ex.Message, ex, Token);
                    throw new Exception(ex.Message, ex);
                }
            }
            return ds.Tables;
        }
        // Executes a parameterised SQL statement asynchronously, used for the remote catalog queries.
        /// <summary>
        /// Executes an SQL statement with named parameters, returning a datatable object
        /// </summary>
        /// <param name="strSQL">SQL Statement using @name parameters</param>
        /// <param name="Args">Parameter values by name, with or without the @</param>
        /// <param name="Token">Cancellation token</param>
        /// <returns>DataTable results from the SQL Statement</returns>
        public async Task<DataTable?> ExecuteSQLAsync(string strSQL, Dictionary<string, object?> Args, CancellationToken Token)
        {
            if (Conn == null)
                return null;
            using (SqlCommand Command = Conn.CreateCommand())
            {
                Command.CommandText = strSQL;
                Command.CommandTimeout = TimeOut;
                foreach (KeyValuePair<string, object?> item in Args)
                    Command.Parameters.AddWithValue("@" + item.Key.TrimStart('@'), item.Value ?? DBNull.Value);
                using (SqlDataReader Reader = await Command.ExecuteReaderAsync(CommandBehavior.Default, Token))
                {
                    DataTable Response = new DataTable();
                    Response.Load(Reader);
                    return Response;
                }
            }
        }
        private static bool IsTextType(SqlDbType Type)
        {
            return Type == SqlDbType.Char || Type == SqlDbType.NChar || Type == SqlDbType.VarChar || Type == SqlDbType.NVarChar || Type == SqlDbType.Text || Type == SqlDbType.NText;
        }
        // Converts text values supplied by clients into the type the parameter expects.
        private object CoerceParameter(SqlParameter Param, object? Value)
        {
            object fixedValue = FixFormat(Param, Value);
            if (fixedValue is string blank && (blank.Length == 0 || (blank.Trim().Length == 0 && !IsTextType(Param.SqlDbType))))
                return DBNull.Value;
            if (fixedValue is string text)
            {
                switch (Param.SqlDbType)
                {
                    case SqlDbType.TinyInt:
                    case SqlDbType.SmallInt:
                    case SqlDbType.Int:
                    case SqlDbType.BigInt:
                        return long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                    case SqlDbType.Decimal:
                    case SqlDbType.Money:
                    case SqlDbType.SmallMoney:
                        return decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                    case SqlDbType.Float:
                    case SqlDbType.Real:
                        return double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                    case SqlDbType.Date:
                    case SqlDbType.DateTime:
                    case SqlDbType.DateTime2:
                    case SqlDbType.SmallDateTime:
                        return DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                    case SqlDbType.DateTimeOffset:
                        return DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                    case SqlDbType.UniqueIdentifier:
                        return Guid.Parse(text);
                }
            }
            return fixedValue;
        }
    }

}
