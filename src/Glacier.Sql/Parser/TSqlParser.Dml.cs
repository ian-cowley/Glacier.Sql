using System;
using System.Collections.Generic;

namespace Glacier.Sql.Parser
{
    public partial class TSqlParser
    {
        private SqlStatement ParseDelete()
        {
            Consume(TokenType.Delete);
            if (Current.Type == TokenType.From)
            {
                Consume(TokenType.From);
            }
            string tableName = Consume(TokenType.Identifier, "Expected table name in DELETE statement").Text;

            SqlExpression? where = null;
            if (Match(TokenType.Where))
            {
                where = ParseExpression(0);
            }

            return new DeleteStatement(tableName, where);
        }

        private SqlStatement ParseUpdate()
        {
            Consume(TokenType.Update);
            string tableName = Consume(TokenType.Identifier, "Expected table name in UPDATE statement").Text;
            Consume(TokenType.Set, "Expected SET keyword in UPDATE statement");

            var assignments = new List<UpdateAssignment>();
            while (true)
            {
                string colName = Consume(TokenType.Identifier, "Expected column identifier in SET clause").Text;
                Consume(TokenType.Equal, "Expected '=' in column assignment");
                var expr = ParseExpression(0);
                assignments.Add(new UpdateAssignment(colName, expr));

                if (Match(TokenType.Comma)) continue;
                break;
            }

            SqlExpression? where = null;
            if (Match(TokenType.Where))
            {
                where = ParseExpression(0);
            }

            return new UpdateStatement(tableName, assignments, where);
        }

        private SqlStatement ParseBegin()
        {
            Consume(TokenType.Begin);
            if (Current.Type == TokenType.Transaction)
            {
                Consume(TokenType.Transaction);
            }
            return new BeginTransactionStatement();
        }

        private SqlStatement ParseCommit()
        {
            Consume(TokenType.Commit);
            if (Current.Type == TokenType.Transaction)
            {
                Consume(TokenType.Transaction);
            }
            return new CommitTransactionStatement();
        }

        private SqlStatement ParseRollback()
        {
            Consume(TokenType.Rollback);
            if (Current.Type == TokenType.Transaction)
            {
                Consume(TokenType.Transaction);
            }
            return new RollbackTransactionStatement();
        }

        private SqlStatement ParseInsert()
        {
            Consume(TokenType.Insert);
            Consume(TokenType.Into);

            string tableName = Consume(TokenType.Identifier, "Expected table name after INSERT INTO").Text;

            List<string>? columns = null;
            if (Match(TokenType.OpenParenthesis))
            {
                columns = new List<string>();
                while (true)
                {
                    columns.Add(Consume(TokenType.Identifier, "Expected column identifier").Text);
                    if (Match(TokenType.Comma)) continue;
                    break;
                }
                Consume(TokenType.CloseParenthesis, "Expected ')' after insert column list");
            }

            if (Current.Type == TokenType.Select)
            {
                var selectQuery = (SelectStatement)ParseSelect();
                return new InsertSelectStatement(tableName, columns, selectQuery);
            }

            Consume(TokenType.Values, "Expected VALUES keyword");
            Consume(TokenType.OpenParenthesis, "Expected '(' before VALUES list");

            var values = new List<SqlExpression>();
            while (true)
            {
                values.Add(ParseExpression(0));
                if (Match(TokenType.Comma)) continue;
                break;
            }

            Consume(TokenType.CloseParenthesis, "Expected ')' to close VALUES list");
            return new InsertStatement(tableName, columns, values);
        }

        private SqlStatement ParseSelect()
        {
            Consume(TokenType.Select);
            var stmt = new SelectStatement();

            // TOP
            if (Match(TokenType.Top))
            {
                var numToken = Consume(TokenType.NumberLiteral, "Expected number after TOP clause");
                stmt.Top = int.Parse(numToken.Text);
            }

            // Projections
            while (true)
            {
                var expr = ParseExpression(0);
                string? alias = null;
                
                if (Match(TokenType.As))
                {
                    alias = Consume(TokenType.Identifier, "Expected alias identifier after AS").Text;
                }
                else if (Current.Type == TokenType.Identifier)
                {
                    alias = Consume(TokenType.Identifier).Text;
                }

                stmt.Projections.Add(new SelectItem(expr, alias));

                if (Match(TokenType.Comma)) continue;
                break;
            }

            // FROM
            if (Match(TokenType.From))
            {
                stmt.From = ParseTableRef();

                // JOINS
                while (true)
                {
                    string? joinType = null;
                    if (Match(TokenType.Inner))
                    {
                        Consume(TokenType.Join);
                        joinType = "INNER";
                    }
                    else if (Match(TokenType.Left))
                    {
                        if (Current.Type == TokenType.Join) Consume(TokenType.Join);
                        joinType = "LEFT";
                    }
                    else if (Match(TokenType.Cross))
                    {
                        Consume(TokenType.Join);
                        joinType = "CROSS";
                    }
                    else if (Current.Type == TokenType.Join)
                    {
                        Consume(TokenType.Join);
                        joinType = "INNER"; // default JOIN is INNER
                    }

                    if (joinType == null) break;

                    var tableRef = ParseTableRef();
                    var join = new SqlJoin(tableRef, joinType);

                    if (joinType != "CROSS")
                    {
                        Consume(TokenType.On, "Expected ON clause for join");
                        join.On = ParseExpression(0);
                    }

                    stmt.Joins.Add(join);
                }

                // WHERE
                if (Match(TokenType.Where))
                {
                    stmt.Where = ParseExpression(0);
                }

                // GROUP BY
                if (Match(TokenType.Group))
                {
                    Consume(TokenType.By, "Expected BY after GROUP");
                    while (true)
                    {
                        stmt.GroupBy.Add(ParseExpression(0));
                        if (Match(TokenType.Comma)) continue;
                        break;
                    }
                }

                // HAVING
                if (Match(TokenType.Having))
                {
                    stmt.Having = ParseExpression(0);
                }

                // ORDER BY
                if (Match(TokenType.Order))
                {
                    Consume(TokenType.By, "Expected BY after ORDER");
                    while (true)
                    {
                        var expr = ParseExpression(0);
                        bool desc = false;
                        if (Match(TokenType.Desc)) desc = true;
                        else if (Match(TokenType.Asc)) desc = false;

                        stmt.OrderBy.Add(new SqlOrderBy(expr, desc));

                        if (Match(TokenType.Comma)) continue;
                        break;
                    }
                }
            }

            return stmt;
        }

        private SqlTableRef ParseTableRef()
        {
            string tableName = Consume(TokenType.Identifier, "Expected table name in FROM clause").Text;
            while (Match(TokenType.Dot))
            {
                tableName += "." + Consume(TokenType.Identifier, "Expected identifier after '.'").Text;
            }
            string? alias = null;

            if (Match(TokenType.As))
            {
                alias = Consume(TokenType.Identifier, "Expected table alias").Text;
            }
            else if (Current.Type == TokenType.Identifier && Current.Type != TokenType.Join && 
                     Current.Type != TokenType.Inner && Current.Type != TokenType.Left && Current.Type != TokenType.Cross &&
                     Current.Type != TokenType.Where && Current.Type != TokenType.Group && Current.Type != TokenType.Order)
            {
                alias = Consume(TokenType.Identifier).Text;
            }

            return new SqlTableSource(tableName, alias);
        }
    }
}
