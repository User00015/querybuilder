using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using I = Inflector;

namespace SqlKata.Compilers
{

    public partial class Compiler
    {
        public string EngineCode;
        public I.Inflector Inflector { get; protected set; }
        public string TablePrefix { get; set; } = "";
        public bool IsDebug = false;

        /// The list of bindings for the current compilation
        protected List<object> bindings = new List<object>();
        protected string separator
        {
            get
            {
                return IsDebug ? "\n" : " ";
            }
        }

        protected string OpeningIdentifier = "\"";
        protected string ClosingIdentifier = "\"";

        public Compiler()
        {
            Inflector = new I.Inflector(new CultureInfo("en"));
        }

        /// <summary>
        /// Compile a single column clause
        /// </summary>
        /// <param name="columns"></param>
        /// <returns></returns>

        public string CompileColumn(AbstractColumn column)
        {
            if (column is RawColumn raw)
            {
                bindings.AddRange(raw.Bindings);
                return WrapIdentifiers(raw.Expression);
            }

            if (column is QueryColumn)
            {
                var clause = (column as QueryColumn);

                var alias = string.IsNullOrWhiteSpace(clause.Query.QueryAlias) ? "" : $" AS {WrapValue(clause.Query.QueryAlias)}";

                return "(" + CompileSelect(clause.Query) + $"){alias}";
            }

            return Wrap((column as Column).Name);

        }

        public SqlResult Compile(Query query)
        {
            query = OnBeforeCompile(query);

            string sql = "";

            // Handle CTEs
            if (query.GetComponents("cte", EngineCode).Any())
            {
                sql += CompileCte(query) + "\n";
            }

            if (query.Method == "insert")
            {
                sql += CompileInsert(query);
            }
            else if (query.Method == "delete")
            {
                sql += CompileDelete(query);
            }
            else if (query.Method == "update")
            {
                sql += CompileUpdate(query);
            }
            else if (query.Method == "aggregate")
            {
                query.ClearComponent("limit")
                .ClearComponent("select")
                .ClearComponent("group")
                .ClearComponent("order");

                sql += CompileSelect(query);
            }
            else
            {
                sql += CompileSelect(query);
            }

            bindings = bindings.Select(x => x is NullValue ? null : x).ToList();

            sql = OnAfterCompile(sql, bindings);
            return new SqlResult(sql, bindings);
        }

        protected virtual Query OnBeforeCompile(Query query)
        {
            return query;
        }

        public virtual string OnAfterCompile(string sql, List<object> bindings)
        {
            return sql;
        }

        public virtual string CompileCte(Query query)
        {
            var clauses = query.GetComponents<AbstractFrom>("cte", EngineCode);

            if (!clauses.Any())
            {
                return "";
            }

            var sql = new List<string>();

            foreach (var cte in clauses)
            {
                if (cte is RawFromClause raw)
                {
                    bindings.AddRange(raw.Bindings);
                    sql.Add($"{WrapValue(raw.Alias)} AS ({WrapIdentifiers(raw.Expression)})");
                }
                else if (cte is QueryFromClause)
                {
                    QueryFromClause clause = (cte as QueryFromClause);
                    sql.Add($"{WrapValue(clause.Alias)} AS ({CompileSelect(clause.Query)})");
                }
            }

            return "WITH " + string.Join(", ", sql) + " ";
        }


        public virtual string CompileSelect(Query query)
        {
            query = OnBeforeSelect(query);

            if (!query.HasComponent("select", EngineCode))
            {
                query.Select("*");
            }

            string sql = "";

            var results = CompileComponents(query);

            sql += JoinComponents(results, "select");

            // Handle UNION, EXCEPT and INTERSECT
            if (query.GetComponents("combine", EngineCode).Any())
            {
                var combinedQueries = new List<string>();

                var clauses = query.GetComponents<AbstractCombine>("combine", EngineCode);

                combinedQueries.Add("(" + sql + ")");

                foreach (var clause in clauses)
                {
                    if (clause is Combine)
                    {
                        var combineClause = clause as Combine;

                        var combineOperator = combineClause.Operation.ToUpper() + " " + (combineClause.All ? "ALL " : "");

                        var compiled = CompileSelect(combineClause.Query);

                        combinedQueries.Add($"{combineOperator}({compiled})");
                    }
                    else
                    {
                        var combineRawClause = clause as RawCombine;
                        combinedQueries.Add(WrapIdentifiers(combineRawClause.Expression));
                    }
                }

                sql = JoinComponents(combinedQueries, "combine");

            }

            return sql;
        }

        protected virtual Query OnBeforeSelect(Query query)
        {
            return query;
        }

        /// <summary>
        /// Compile INSERT into statement
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        protected virtual string CompileInsert(Query query)
        {
            if (!query.HasComponent("from", EngineCode))
            {
                throw new InvalidOperationException("No table set to insert");
            }

            var from = query.GetOneComponent<AbstractFrom>("from", EngineCode);

            if (!(from is FromClause))
            {
                throw new InvalidOperationException("Invalid table expression");
            }

            string sql = "";

            var inserts = query.GetComponents<AbstractInsertClause>("insert", EngineCode);

            if (inserts[0] is InsertClause)
            {
                var clause = inserts[0] as InsertClause;

                sql += "INSERT INTO " + CompileTableExpression(from)
                + " (" + string.Join(", ", WrapArray(clause.Columns)) + ") "
                + "VALUES (" + string.Join(", ", Parameterize(clause.Values)) + ")";
            }
            else
            {
                var clause = inserts[0] as InsertQueryClause;

                var columns = "";

                if (clause.Columns.Any())
                {
                    columns = $"({string.Join(", ", WrapArray(clause.Columns))}) ";
                }

                sql += "INSERT INTO " + CompileTableExpression(from)
                + " " + columns + CompileSelect(clause.Query);
            }

            if (inserts.Count > 1)
            {
                foreach (var insert in inserts.GetRange(1, inserts.Count() - 1))
                {
                    var clause = insert as InsertClause;

                    sql += ", (" + string.Join(", ", Parameterize(clause.Values)) + ")";

                }
            }


            return sql;

        }


        protected virtual string CompileUpdate(Query query)
        {
            if (!query.HasComponent("from", EngineCode))
            {
                throw new InvalidOperationException("No table set to update");
            }

            var from = query.GetOneComponent<AbstractFrom>("from", EngineCode);

            if (!(from is FromClause))
            {
                throw new InvalidOperationException("Invalid table expression");
            }

            var toUpdate = query.GetOneComponent<InsertClause>("update", EngineCode);

            var parts = new List<string>();
            string sql;

            for (var i = 0; i < toUpdate.Columns.Count; i++)
            {
                parts.Add($"{Wrap(toUpdate.Columns[i])} = ?");
            }

            bindings.AddRange(toUpdate.Values);

            var where = CompileWheres(query);

            if (!string.IsNullOrEmpty(where))
            {
                where = " " + where;
            }

            sql = "UPDATE " + CompileTableExpression(from)
                + " SET " + string.Join(", ", parts)
                + where;

            return sql;
        }

        protected virtual string CompileDelete(Query query)
        {
            if (!query.HasComponent("from", EngineCode))
            {
                throw new InvalidOperationException("No table set to delete");
            }

            var from = query.GetOneComponent<AbstractFrom>("from", EngineCode);

            if (!(from is FromClause))
            {
                throw new InvalidOperationException("Invalid table expression");
            }

            string sql;

            var where = CompileWheres(query);

            if (!string.IsNullOrEmpty(where))
            {
                where = " " + where;
            }

            sql = "DELETE FROM " + CompileTableExpression(from) + where;

            return sql;
        }

        protected List<string> CompileComponents(Query query)
        {
            var result = (new List<string>
            {
                this.CompileAggregate(query),
                this.CompileColumns(query),
                this.CompileFrom(query),
                this.CompileJoins(query),
                this.CompileWheres(query),
                this.CompileGroups(query),
                this.CompileHavings(query),
                this.CompileOrders(query),
                this.CompileLimit(query),
                this.CompileOffset(query),
                this.CompileLock(query),
            })
            .ToList()
            .Where(x => x != null)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrEmpty(x))
            .ToList();

            return result;
        }


        protected virtual string CompileColumns(Query query)
        {
            // If the query is actually performing an aggregating select, we will let that
            // compiler handle the building of the select clauses, as it will need some
            // more syntax that is best handled by that function to keep things neat.
            if (query.HasComponent("aggregate", EngineCode))
            {
                return null;
            }

            if (!query.HasComponent("select", EngineCode))
            {
                return null;
            }

            var columns = query.GetComponents("select", EngineCode).Cast<AbstractColumn>().ToList();

            var cols = columns.Select(CompileColumn).ToArray();

            var select = (query.IsDistinct ? "SELECT DISTINCT " : "SELECT ");

            return select + (cols.Any() ? string.Join(", ", cols) : "*");
        }

        protected virtual string CompileAggregate(Query query)
        {

            if (!query.HasComponent("aggregate", EngineCode))
            {
                return null;
            }

            var ag = query.GetComponents("aggregate").Cast<AggregateClause>().First();

            var columns = ag.Columns
                .Select(x => new Column { Name = x })
                .Cast<AbstractColumn>()
                .ToList();

            var cols = columns.Select(x => CompileColumn(x));

            var sql = string.Join(", ", cols);

            if (query.IsDistinct)
            {
                sql = "DISTINCT " + sql;
            }

            return "SELECT " + ag.Type.ToUpper() + "(" + sql + ") AS " + WrapValue(ag.Type);
        }

        protected virtual string CompileTableExpression(AbstractFrom from)
        {
            if (from is RawFromClause raw)
            {
                bindings.AddRange(raw.Bindings);
                return WrapIdentifiers(raw.Expression);
            }

            if (from is QueryFromClause)
            {
                var fromQuery = (from as QueryFromClause).Query;

                var alias = string.IsNullOrEmpty(fromQuery.QueryAlias) ? "" : " AS " + WrapValue(fromQuery.QueryAlias);

                var compiled = CompileSelect(fromQuery);

                return "(" + compiled + ")" + alias;
            }

            if (from is FromClause)
            {
                return WrapTable((from as FromClause).Table);
            }

            throw InvalidClauseException("TableExpression", from);
        }

        protected virtual string CompileFrom(Query query)
        {
            if (!query.HasComponent("from", EngineCode))
            {
                return null;
            }

            var from = query.GetOneComponent<AbstractFrom>("from", EngineCode);

            return "FROM " + CompileTableExpression(from);
        }

        protected virtual string CompileJoins(Query query)
        {
            if (!query.HasComponent("join", EngineCode))
            {
                return null;
            }

            // Transfrom deep join expressions to regular join

            var deepJoins = query.GetComponents<AbstractJoin>("join", EngineCode).OfType<DeepJoin>().ToList();

            foreach (var deepJoin in deepJoins)
            {
                var index = query.Clauses.IndexOf(deepJoin);

                query.Clauses.Remove(deepJoin);
                foreach (var join in TransfromDeepJoin(query, deepJoin))
                {
                    query.Clauses.Insert(index, join);
                    index++;
                }
            }

            var joins = query.GetComponents<BaseJoin>("join", EngineCode);

            var sql = new List<string>();

            foreach (var item in joins)
            {
                sql.Add(CompileJoin(item.Join));
            }

            return JoinComponents(sql, "join");
        }

        protected virtual string CompileJoin(Join join, bool isNested = false)
        {

            var from = join.GetOneComponent<AbstractFrom>("from", EngineCode);
            var conditions = join.GetComponents<AbstractCondition>("where", EngineCode);

            var joinTable = CompileTableExpression(from);
            var constraints = CompileConditions(conditions);

            var onClause = conditions.Any() ? $" ON {constraints}" : "";

            return $"{join.Type} JOIN {joinTable}{onClause}";
        }

        protected virtual string CompileWheres(Query query)
        {
            if (!query.HasComponent("from", EngineCode) || !query.HasComponent("where", EngineCode))
            {
                return null;
            }

            var conditions = query.GetComponents<AbstractCondition>("where", EngineCode);
            var sql = CompileConditions(conditions).Trim();

            if (string.IsNullOrEmpty(sql))
            {
                return null;
            }

            return $"WHERE {sql}";
        }

        protected string CompileQuery<T>(
                BaseQuery<T> query,
                string joinType = "",
                bool isNested = false
        ) where T : BaseQuery<T>
        {
            if (query is Query)
            {
                return CompileSelect(query as Query);
            }

            if (query is Join)
            {
                return CompileJoin((query as Join), isNested);
            }

            return "";
        }

        protected virtual string CompileGroups(Query query)
        {
            if (!query.HasComponent("group", EngineCode))
            {
                return null;
            }

            var columns = query.GetComponents<AbstractColumn>("group", EngineCode).Select(x => CompileColumn(x));

            return "GROUP BY " + string.Join(", ", columns);
        }

        protected virtual string CompileOrders(Query query)
        {
            if (!query.HasComponent("order", EngineCode))
            {
                return null;
            }

            var columns = query.GetComponents<AbstractOrderBy>("order", EngineCode).Select(x =>
            {

                if (x is RawOrderBy raw)
                {
                    bindings.AddRange(raw.Bindings);
                    return WrapIdentifiers(raw.Expression);
                }

                var direction = (x as OrderBy).Ascending ? "" : " DESC";

                return Wrap((x as OrderBy).Column) + direction;
            });

            return "ORDER BY " + string.Join(", ", columns);
        }

        public string CompileHavings(Query query)
        {
            if (!query.HasComponent("having", EngineCode))
            {
                return null;
            }

            var sql = new List<string>();
            string boolOperator;

            var havings = query.GetComponents("having", EngineCode)
                .Cast<AbstractCondition>()
                .ToList();

            for (var i = 0; i < havings.Count; i++)
            {
                var compiled = CompileCondition(havings[i]);

                if (!string.IsNullOrEmpty(compiled))
                {
                    boolOperator = i > 0 ? havings[i].IsOr ? "OR " : "AND " : "";

                    sql.Add(boolOperator + "HAVING " + compiled);
                }
            }

            return JoinComponents(sql, "having");
        }

        public virtual string CompileLimit(Query query)
        {
            var limitOffset = query.GetOneComponent("limit", EngineCode) as LimitOffset;

            if (limitOffset != null && limitOffset.HasLimit())
            {
                bindings.Add(limitOffset.Limit);
                return "LIMIT ?";
            }

            return "";
        }

        public virtual string CompileOffset(Query query)
        {
            var limitOffset = query.GetOneComponent("limit", EngineCode) as LimitOffset;

            if (limitOffset != null && limitOffset.HasOffset())
            {
                bindings.Add(limitOffset.Offset);
                return "OFFSET ?";
            }

            return "";
        }

        protected virtual string CompileLock(Query query)
        {
            // throw new NotImplementedException();
            return null;
        }

        /// <summary>
        /// Compile the random statement into SQL.
        /// </summary>
        /// <param name="seed"></param>
        /// <returns></returns>
        public virtual string CompileRandom(string seed)
        {
            return "RANDOM()";
        }

        public virtual string CompileLower(string value)
        {
            return $"LOWER({value})";
        }

        public virtual string CompileUpper(string value)
        {
            return $"UPPER({value})";
        }

        private InvalidCastException InvalidClauseException(string section, AbstractClause clause)
        {
            return new InvalidCastException($"Invalid type \"{clause.GetType().Name}\" provided for the \"{section}\" clause.");
        }

        protected string dynamicCompile(string name, AbstractClause clause)
        {

            MethodInfo methodInfo = this.GetType()
                .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);

            if (methodInfo == null)
            {
                throw new Exception($"Failed to locate a compiler for {name}.");
            }

            var isGeneric = Helper.IsGenericType(clause.GetType());

            if (isGeneric && methodInfo.GetGenericArguments().Any())
            {
                var args = clause.GetType().GetGenericArguments();
                methodInfo = methodInfo.MakeGenericMethod(args);
            }

            var result = methodInfo.Invoke(this, new object[] { clause });

            return result as string;
        }

        protected virtual IEnumerable<BaseJoin> TransfromDeepJoin(Query query, DeepJoin join)
        {
            var exp = join.Expression;

            var tokens = exp.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

            if (!tokens.Any())
            {
                yield break;
            }


            var from = query.GetOneComponent<AbstractFrom>("from", EngineCode);

            if (from == null)
            {
                yield break;
            }

            string tableOrAlias = from.Alias;

            if (string.IsNullOrEmpty(tableOrAlias))
            {
                throw new InvalidOperationException("No table or alias found for the main query, This information is needed in order to generate a Deep Join");
            }

            for (var i = 0; i < tokens.Length; i++)
            {
                var source = i == 0 ? tableOrAlias : tokens[i - 1];
                var target = tokens[i];

                string sourceKey;
                string targetKey;

                if (join.SourceKeyGenerator != null)
                {
                    // developer wants to use the lambda overloaded method then
                    sourceKey = join.SourceKeyGenerator.Invoke(target);
                    targetKey = join.TargetKeyGenerator?.Invoke(target) ?? "Id";
                }
                else
                {
                    sourceKey = Singular(target) + join.SourceKeySuffix;
                    targetKey = join.TargetKey;
                }

                // yield query.Join(target, $"{source}.{sourceKey}", $"{target}.{targetKey}", "=", join.Type);
                yield return new BaseJoin
                {
                    Component = "join",
                    Join = new Join().AsType(join.Type).JoinWith(target).On
                    ($"{source}.{sourceKey}", $"{target}.{targetKey}", "=")
                };
            }

        }

        protected string JoinComponents(List<string> components, string section = null)
        {
            return string.Join(separator, components);
        }

        /// <summary>
        /// Wrap a table in keyword identifiers.
        /// </summary>
        /// <param name="table"></param>
        /// <returns></returns>
        public virtual string WrapTable(string table)
        {
            return Wrap(this.TablePrefix + table, true);
        }

        /// <summary>
        /// Wrap a single string in a column identifier.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public virtual string Wrap(string value, bool prefixAlias = false)
        {
            if (value.ToLower().Contains(" as "))
            {
                var segments = value.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (prefixAlias)
                {
                    segments[2] = this.TablePrefix + segments[2];
                }

                return Wrap(segments[0]) + " AS " + WrapValue(segments[2]);
            }

            if (value.Contains("."))
            {
                return string.Join(".", value.Split('.').Select((x, index) =>
                {
                    // Wrap the first segment as table
                    if (index == 0)
                    {
                        return WrapTable(x);
                    }

                    return WrapValue(x);

                }));
            }

            // If we reach here then the value does not contain an "AS" alias
            // nor dot "." expression, so wrap it as regular value.
            return WrapValue(value);
        }

        public virtual string Wrap(Raw value)
        {
            return WrapIdentifiers(value.Value);
        }

        /// <summary>
        /// Wrap a single string in keyword identifiers.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public virtual string WrapValue(string value)
        {
            if (value == "*") return value;

            var opening = this.OpeningIdentifier;
            var closing = this.ClosingIdentifier;

            return opening + value.Replace(closing, closing + closing) + closing;
        }

        public string Parameter<T>(T value)
        {
            if (value is Raw)
            {
                var raw = value as Raw;
                bindings.AddRange(raw.Bindings);

                return WrapIdentifiers(raw.Value);
            }

            bindings.Add(value);
            return "?";
        }

        /// <summary>
        /// Create query parameter place-holders for an array.
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public string Parameterize<T>(IEnumerable<T> values)
        {
            return string.Join(", ", values.Select(x => Parameter(x)));
        }

        /// <summary>
        /// Wrap an array of values.
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public List<string> WrapArray(List<string> values)
        {
            return values.Select(x => Wrap(x)).ToList();
        }

        public string WrapIdentifiers(string input)
        {
            return input

                // deprecated
                .Replace("{", this.OpeningIdentifier)
                // deprecated
                .Replace("}", this.ClosingIdentifier)

                .Replace("[", this.OpeningIdentifier)
                .Replace("]", this.ClosingIdentifier);
        }

        public virtual string Singular(string value)
        {
            return Inflector.Singularize(value);
        }

        public virtual string Plural(string value)
        {
            return Inflector.Pluralize(value);
        }


    }



}