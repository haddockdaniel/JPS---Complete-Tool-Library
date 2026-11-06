using System;
using Jps.Core.Data;

namespace Jps.Tools.Common
{
    /// <summary>
    /// Adds a new batch to the Juris batch tree (DocumentTree) the way Juris does: under the batch type's root folder, inside
    /// the user / accounting-period folders the firm option asks for (created when missing). Ids come from
    /// <see cref="JurisCounters.NextDocTreeId"/>. Call only inside Session.InTransaction.
    /// </summary>
    public static class JurisBatchTree
    {
        /// <summary>
        /// Puts batch <paramref name="batch"/> of DocumentTree class <paramref name="docClass"/> (e.g. 7200 vouchers,
        /// 7500 trust adjustments) in the tree. <paramref name="typeName"/> names the batch type in messages
        /// ("Payment Voucher"); <paramref name="userFolder"/> is the user folder name when the firm uses user folders.
        /// </summary>
        public static void AddBatch(JurisSession session, int docClass, string typeName, int batch, string title, string userFolder)
        {
            var root = session.Query("exec qsDocumentTreeRootFldByCls @cls", Sql.P("@cls", docClass));
            if (root.Rows.Count == 0)
                throw new InvalidOperationException("Juris has no " + typeName + " folder in its batch list, so the batch was not created. Open Juris once to check the batch list, then try again.");
            var parent = Convert.ToInt32(root.Rows[0]["DTDocID"]);

            var periodTitle = Convert.ToString(session.Scalar(
                @"select (select cast(SpNbrValue as char(4)) from SysParam where SpName = 'CurAcctPrdYear') + '-' +
                         (select right('00' + cast(SpNbrValue as varchar(2)), 2) from SysParam where SpName = 'CurAcctPrdNbr')"));

            switch (FolderOption(session))
            {
                case "0":
                    break;
                case "1":
                    parent = EnsureFolder(session, docClass, parent, userFolder);
                    parent = EnsureFolder(session, docClass, parent, RequirePeriodTitle(periodTitle));
                    break;
                case "2":
                    parent = EnsureFolder(session, docClass, parent, RequirePeriodTitle(periodTitle));
                    parent = EnsureFolder(session, docClass, parent, userFolder);
                    break;
                default:
                    throw new InvalidOperationException("The firm option that says which folders new batches go in (by user and/or accounting period) has a value this tool doesn't recognise, so the batch was not created. Check that option in Juris' firm options.");
            }

            session.Execute(
                @"insert into DocumentTree (DTDocID, DTSystemCreated, DTDocClass, DTDocType, DTParentID, DTTitle, DTKeyL, DTKeyT)
                  values (@id, 'Y', @cls, 'R', @parent, @title, @batch, null)",
                Sql.P("@id", JurisCounters.NextDocTreeId(session)), Sql.P("@cls", docClass), Sql.P("@parent", parent),
                Sql.P("@title", title), Sql.P("@batch", batch));
        }

        /// <summary>
        /// The firm's batch-folder option: the 7th field of the comma list in SysParam CfgMiscOpts ("0" no folders, "1" user
        /// then period, "2" period then user). Read by field, not by character position (the original's substring(…, 14, 1)
        /// only worked when the first field was exactly two characters and failed for every other firm). No CfgMiscOpts at
        /// all, or too few fields = "0" as before; a blank field = "1", as Juris itself treats it.
        /// </summary>
        public static string FolderOption(JurisSession session)
        {
            var text = session.Scalar("select SpTxtValue from SysParam where SpName = 'CfgMiscOpts'");
            if (text == null)
                return "0";
            var fields = Convert.ToString(text).Split(',');
            if (fields.Length < 7)
                return "0"; // too short to hold the option: root folder, as the original did
            var value = fields[6].Trim();
            return value.Length == 0 ? "1" : value;
        }

        private static string RequirePeriodTitle(string periodTitle)
        {
            if (string.IsNullOrWhiteSpace(periodTitle))
                throw new InvalidOperationException("Juris' current accounting period isn't set, so the batch can't be put in its period folder. No batch was created. Set the current accounting period in Juris and try again.");
            return periodTitle.Trim();
        }

        private static int EnsureFolder(JurisSession session, int docClass, int parent, string title)
        {
            var found = session.Query("exec qsDocumentTreeByClassTypeParentIDTitle @cls, 'F', @parent, @title",
                Sql.P("@cls", docClass), Sql.P("@parent", parent), Sql.P("@title", title));
            if (found.Rows.Count > 0)
                return Convert.ToInt32(found.Rows[0]["DTDocID"]);

            var id = JurisCounters.NextDocTreeId(session);
            session.Execute(
                @"insert into DocumentTree (DTDocID, DTSystemCreated, DTDocClass, DTDocType, DTParentID, DTTitle, DTKeyL, DTKeyT)
                  values (@id, 'Y', @cls, 'F', @parent, @title, null, null)",
                Sql.P("@id", id), Sql.P("@cls", docClass), Sql.P("@parent", parent), Sql.P("@title", title));
            return id;
        }
    }
}
