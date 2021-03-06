using System;
using System.Collections.Generic;
using System.Linq;
using System.Data.SqlClient;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Reflection;

namespace BulkInsert
{
    public class BulkSQLWriter
    {
        public string ConnectionString { get; private set; }
        private Dictionary<string, string> ConvertToSqlType = new Dictionary<string, string>();

        public BulkSQLWriter(string connectionString)
        {
            ConnectionString = connectionString;
            ConvertToSqlType.Add("System.Boolean", "bit");
            ConvertToSqlType.Add("System.Byte", "tinyint");
            ConvertToSqlType.Add("System.Char", "char");
            ConvertToSqlType.Add("System.DateTime", "time");
            ConvertToSqlType.Add("System.Decimal", "decimal");
            ConvertToSqlType.Add("System.Double", "float");
            ConvertToSqlType.Add("System.Int16", "smallint");
            ConvertToSqlType.Add("System.Int32", "int");
            ConvertToSqlType.Add("System.Int64", "bigint");
            ConvertToSqlType.Add("System.SByte", "int");
            ConvertToSqlType.Add("System.Single", "real");
            ConvertToSqlType.Add("System.String", "nvarchar(4000)");
            ConvertToSqlType.Add("System.TimeSpan", "time");
            ConvertToSqlType.Add("System.UInt16", "int");
            ConvertToSqlType.Add("System.UInt32", "int");
            ConvertToSqlType.Add("System.UInt64", "bigint");
        }

        public int BulkInsert<T>(IEnumerable<T> list, string tableName = null) where T : class
        {
            tableName = GetTableName<T>(tableName);
            DataTable table = ConvertToDataTable(list, tableName);
            using (var bulkCopy = new SqlBulkCopy(ConnectionString))
            {
                bulkCopy.DestinationTableName = tableName;
                bulkCopy.BatchSize = list.Count();
                bulkCopy.BulkCopyTimeout = 200;
                bulkCopy.WriteToServer(table);
                return table.Rows.Count;
            }
        }

        public void UpdateData<T>(IEnumerable<T> list, string sqlUpdate, string tableName = null, bool keepTable = false) where T : class
        {
            tableName = GetTableName<T>(tableName);
            string tempTableName = "#TmpTable" + tableName;
            DataTable dt = ConvertToDataTable(list, tableName);
            // ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
            using (SqlConnection conn = new SqlConnection(ConnectionString))
            using (SqlCommand command = new SqlCommand("", conn))
            {
                try
                {
                    conn.Open();

                    command.CommandText = $"SELECT OBJECT_ID('{tempTableName}')";
                    object exists = command.ExecuteScalar();

                    if (exists != null && DBNull.Value == exists)
                    { 
                        //Creating temp table on database
                        command.CommandText = $"CREATE TABLE {tempTableName} ( {GetColumns(dt)} )";
                        command.ExecuteNonQuery();
                    }

                    //Bulk insert into temp table
                    using (SqlBulkCopy bulkcopy = new SqlBulkCopy(conn))
                    {
                        bulkcopy.BulkCopyTimeout = 660;
                        bulkcopy.DestinationTableName = tempTableName;
                        bulkcopy.WriteToServer(dt);
                        bulkcopy.Close();
                    }

                    // Updating destination table, and dropping temp table
                    command.CommandTimeout = 300;
                    if(keepTable)
                        command.CommandText = sqlUpdate + "; TRUNCATE TABLE " + tempTableName;
                    else
                        command.CommandText = sqlUpdate + "; DROP TABLE " + tempTableName;

                    command.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    throw new Exception("Unable to BulkUpdate " + tableName, ex);
                }
                finally
                {
                    conn.Close();
                }
            }
        }

        internal DataTable BulkSelect(DataTable dtJoin, string joinSelect, string tableName = null, bool keepTable = false)
        {
            string tempTableName = "#TmpTable" + tableName;
            using (SqlConnection conn = new SqlConnection(ConnectionString))
            using (SqlCommand command = new SqlCommand("", conn))
            {
                try
                {
                    conn.Open();

                    command.CommandText = $"SELECT OBJECT_ID('{tempTableName}')";
                    object exists = command.ExecuteScalar();

                    if (exists != null && DBNull.Value == exists)
                    {
                        //Creating temp table on database
                        command.CommandText = $"CREATE TABLE {tempTableName} ( {GetColumns(dtJoin)} )";
                        command.ExecuteNonQuery();
                    }

                    //Bulk insert into temp table
                    using (SqlBulkCopy bulkcopy = new SqlBulkCopy(conn))
                    {
                        bulkcopy.BulkCopyTimeout = 660;
                        bulkcopy.DestinationTableName = tempTableName;
                        bulkcopy.WriteToServer(dtJoin);
                        bulkcopy.Close();
                    }

                    command.CommandTimeout = 300;
                    if (keepTable)
                        joinSelect = joinSelect + "; TRUNCATE TABLE " + tempTableName;
                    else
                        joinSelect = joinSelect + "; DROP TABLE " + tempTableName;

                    var dtResult = new DataTable();
                    var da = new SqlDataAdapter(joinSelect, conn);
                    da.Fill(dtResult);
                    return dtResult;                    
                }
                catch (Exception ex)
                {
                    throw new Exception("Unable to BulkSelect " + tableName, ex);
                }
                finally
                {
                    conn.Close();
                }
            }
        }

        private string GetColumns(DataTable dt)
        {
            string output = string.Empty;
            foreach (DataColumn col in dt.Columns)
                output += col.ColumnName + " " + ConvertToSqlType[col.DataType.ToString()] + ", ";

            return output.Substring(0, output.Length-2);  // remove the last ','
        }

        public void DropTempTableFor(string tableName)
        {
            using (SqlConnection conn = new SqlConnection(ConnectionString))
            using (SqlCommand command = new SqlCommand("", conn))
            {
                try
                {
                    conn.Open();

                    command.CommandText = $"SELECT OBJECT_ID('#TmpTable{tableName}')";
                    object exists = command.ExecuteScalar();

                    if (exists != null && DBNull.Value != exists)
                    {
                        //Creating temp table on database
                        command.CommandText = $"DROP TABLE #TmpTable{tableName}";
                        command.ExecuteNonQuery();
                    }
                }
                catch(Exception ex)
                {
                    throw new Exception("Unable to drop table " + tableName, ex);
                }
            }
        }

        private DataTable ConvertToDataTable<T>(IEnumerable<T> list, string tableName) where T : class
        {
            var table = new DataTable(tableName);
            var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                       //Dirty hack to make sure we only have system data types 
                                       //i.e. filter out the relationships/collections
                                       .Where(x => x.CanRead && x.CanWrite && x.PropertyType.Namespace != null && x.PropertyType.Namespace.Equals("System"))
                                       .Where(y => y.CustomAttributes.Any(z => z.AttributeType.Name == "DataMemberAttribute"))
                                       .ToList();

            foreach (var x in props)
            {
                // bulkCopy.ColumnMappings.Add(x.Name, x.Name);
                var type = Nullable.GetUnderlyingType(x.PropertyType) ?? x.PropertyType;
                table.Columns.Add(x.Name, type);
                // Debug.Print("[" + x.Name + "]   " + type.ToString());
            }

            var values = new object[props.Count()];
            foreach (var item in list)
            {
                for (var i = 0; i < values.Length; i++)
                    values[i] = props[i].GetValue(item);

                table.Rows.Add(values);
            }

            return table;
        }

        private static string GetTableName<T>(string tableName) where T : class
        {
            TableAttribute tableAttribute = (TableAttribute)Attribute.GetCustomAttribute(typeof(T), typeof(TableAttribute));
            if (tableAttribute != null && tableName == null)
                tableName = tableAttribute.Name;

            if (string.IsNullOrWhiteSpace(tableName))
                throw new Exception("The Table name was not found in BulkInsert.");

            return tableName;
        }

    }
}
