﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data;
using System.Data.OleDb;
using System.Drawing.Design;
using System.Linq;
using TabularEditor.PropertyGridUI;
using TabularEditor.TextServices;
using TabularEditor.TOMWrapper.Utils;
using TabularEditor.TOMWrapper.Undo;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularEditor.TOMWrapper
{
    partial class Table: ITabularObjectContainer, IDetailObjectContainer, ITabularPerspectiveObject, IDaxObject,
        IErrorMessageObject, IDaxDependantObject, IExpressionObject
    {
        private DependsOnList _dependsOn = null;

        [Browsable(false)]
        public DependsOnList DependsOn
        {
            get
            {
                if (_dependsOn == null)
                    _dependsOn = new DependsOnList(this);
                return _dependsOn;
            }
        }

        [Browsable(false)]
        public ReferencedByList ReferencedBy { get; } = new ReferencedByList();

        protected override bool AllowDelete(out string message)
        {
            message = string.Empty;
            if (ReferencedBy.Count > 0) message += Messages.ReferencedByDAX;
            if (message == string.Empty) message = null;
            return true;
        }

        #region Convenient methods
        [IntelliSense("Adds a new measure to the table."), Tests.GenerateTest()]
        public Measure AddMeasure(string name = null, string expression = null, string displayFolder = null)
        {
            Handler.BeginUpdate("add measure");
            var measure = Measure.CreateNew(this, name);
            if (!string.IsNullOrEmpty(expression)) measure.Expression = expression;
            if (!string.IsNullOrEmpty(displayFolder)) measure.DisplayFolder = displayFolder;
            Handler.EndUpdate();
            return measure;
        }

        [IntelliSense("Adds a new calculated column to the table."),Tests.GenerateTest()]
        public CalculatedColumn AddCalculatedColumn(string name = null, string expression = null, string displayFolder = null)
        {
            Handler.BeginUpdate("add calculated column");
            var column = CalculatedColumn.CreateNew(this, name);
            if (!string.IsNullOrEmpty(expression)) column.Expression = expression;
            if (!string.IsNullOrEmpty(displayFolder)) column.DisplayFolder = displayFolder;
            Handler.EndUpdate();
            return column;
        }

        [IntelliSense("Adds a new Data column to the table."), Tests.GenerateTest()]
        public DataColumn AddDataColumn(string name = null, string sourceColumn = null, string displayFolder = null)
        {
            if (Handler.UsePowerBIGovernance && !PowerBI.PowerBIGovernance.AllowCreate(typeof(DataColumn))) return null;

            Handler.BeginUpdate("add Data column");
            var column = DataColumn.CreateNew(this, name);
            column.DataType = DataType.String;
            if (!string.IsNullOrEmpty(sourceColumn)) column.SourceColumn = sourceColumn;
            if (!string.IsNullOrEmpty(displayFolder)) column.DisplayFolder = displayFolder;
            Handler.EndUpdate();
            return column;
        }


        [IntelliSense("Adds a new hierarchy to the table."), Tests.GenerateTest()]
        public Hierarchy AddHierarchy(string name = null, string displayFolder = null, params Column[] levels)
        {
            Handler.BeginUpdate("add hierarchy");
            var hierarchy = Hierarchy.CreateNew(this, name);
            if (!string.IsNullOrEmpty(displayFolder)) hierarchy.DisplayFolder = displayFolder;
            for(var i = 0; i < levels.Length; i++)
            {
                hierarchy.AddLevel(levels[i], ordinal: i);
            }
            Handler.EndUpdate();
            return hierarchy;
        }
        
        [IntelliSense("Adds a new hierarchy to the table.")]
        public Hierarchy AddHierarchy(string name, string displayFolder = null, params string[] levels)
        {
            return AddHierarchy(name, displayFolder, levels.Select(s => Columns[s]).ToArray());
        }

        #endregion
        #region Convenient Collections
        /// <summary>
        /// Enumerates all levels across all hierarchies on this table.
        /// </summary>
        [Browsable(false)]
        public IEnumerable<Level> AllLevels { get { return Hierarchies.SelectMany(h => h.Levels); } }
        /// <summary>
        /// Enumerates all relationships in which this table participates.
        /// </summary>
        [Browsable(false)]
        public IEnumerable<SingleColumnRelationship> UsedInRelationships { get { return Model.Relationships.Where(r => r.FromTable == this || r.ToTable == this); } }
        /// <summary>
        /// Enumerates all tables related to or from this table.
        /// </summary>
        [Browsable(false)]
        public IEnumerable<Table> RelatedTables
        {
            get
            {
                return UsedInRelationships.Select(r => r.FromTable)
                    .Concat(UsedInRelationships.Select(r => r.ToTable))
                    .Where(t => t != this).Distinct();
            }
        }
        #endregion

        internal override void DeleteLinkedObjects(bool isChildOfDeleted)
        {
            // Clear folder cache:
            Handler.Tree.FolderCache.Clear();

            // Remove row-level-security for this table:
            RowLevelSecurity.Clear();
            if(Handler.CompatibilityLevel >= 1400) ObjectLevelSecurity.Clear();

            base.DeleteLinkedObjects(isChildOfDeleted);
        }

        [Browsable(false)]
        public Table ParentTable { get { return this; } }

        [Category("Data Source")]
        public string Source {
            get
            {
                var ds = (MetadataObject.Partitions.FirstOrDefault().Source as TOM.QueryPartitionSource)?.DataSource;
                string sourceName = null;
                if (ds != null) sourceName = (Handler.WrapperLookup[ds] as DataSource)?.Name;
                return sourceName ?? ds?.Name;
            }
        }
        [Category("Data Source"), DisplayName("Source Type")]
        public TOM.PartitionSourceType SourceType
        {
            get
            {
                return MetadataObject.GetSourceType();
            }
        }

        protected override bool IsBrowsable(string propertyName)
        {
            switch(propertyName)
            {
                case "Source":
                case "Partitions":
                    return SourceType == TOM.PartitionSourceType.Query || SourceType == TOM.PartitionSourceType.M;
                case "DefaultDetailRowsExpression":
                case "ShowAsVariationsOnly":
                case "IsPrivate":
                    return Handler.CompatibilityLevel >= 1400;
                case "ObjectLevelSecurity":
                    return Handler.CompatibilityLevel >= 1400 && Model.Roles.Any();
                case "RowLevelSecurity":
                    return Model.Roles.Any();
                default: return true;
            }
        }

        [Browsable(true), DisplayName("Row Level Security"), Category("Security")]
        public TableRLSIndexer RowLevelSecurity { get; private set; }

        [Browsable(false)]
        public string DaxObjectName
        {
            get
            {
                return string.Format("'{0}'", Name);
            }
        }

        [Browsable(true), Category("Metadata"), DisplayName("DAX identifier")]
        public string DaxObjectFullName
        {
            get
            {
                return DaxObjectName;
            }
        }

        [Browsable(false)]
        public string DaxTableName
        {
            get
            {
                return DaxObjectName;
            }
        }

        /// <summary>
        /// Returns all columns, measures and hierarchies inside this table.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<ITabularNamedObject> GetChildren()
        {
            return Columns.Concat<TabularNamedObject>(Measures).Concat(Hierarchies);
        }

        public IEnumerable<IDetailObject> GetChildrenByFolders(bool recursive)
        {
            throw new InvalidOperationException();
        }

        [Browsable(false)]
        public PartitionViewTable PartitionViewTable { get; private set; }

        protected override void Init()
        {
            PartitionViewTable = new PartitionViewTable(this);

            if (Partitions.Count == 0 && !(this is CalculatedTable))
            {
                // Make sure the table contains at least one partition (Calculated Tables handles this on their own):
                Partition.CreateNew(this, Name);
            }

            RowLevelSecurity = new TableRLSIndexer(this);

            if (Handler.CompatibilityLevel >= 1400)
            {
                ObjectLevelSecurity = new TableOLSIndexer(this);
            }

            CheckChildrenErrors();

            base.Init();
        }

        private TableOLSIndexer _objectLevelSecurtiy;
        [DisplayName("Object Level Security"), Category("Security")]
        public TableOLSIndexer ObjectLevelSecurity
        {
            get
            {
                if (Handler.CompatibilityLevel < 1400) throw new InvalidOperationException(Messages.CompatibilityError_ObjectLevelSecurity);
                return _objectLevelSecurtiy;
            }
            set
            {
                if (Handler.CompatibilityLevel < 1400) throw new InvalidOperationException(Messages.CompatibilityError_ObjectLevelSecurity);
                _objectLevelSecurtiy = value;
            }
        }
        private bool ShouldSerializeObjectLevelSecurity() { return false; }

        internal static readonly Dictionary<Type, DataType> DataTypeMapping =
            new Dictionary<Type, DataType>() {
                { typeof(string), DataType.String },
                { typeof(char), DataType.String },
                { typeof(byte), DataType.Int64 },
                { typeof(sbyte), DataType.Int64 },
                { typeof(short), DataType.Int64 },
                { typeof(ushort), DataType.Int64 },
                { typeof(int), DataType.Int64 },
                { typeof(uint), DataType.Int64 },
                { typeof(long), DataType.Int64 },
                { typeof(ulong), DataType.Int64 },
                { typeof(float), DataType.Double },
                { typeof(double), DataType.Double },
                { typeof(decimal), DataType.Decimal },
                { typeof(bool), DataType.Boolean },
                { typeof(DateTime), DataType.DateTime },
                { typeof(byte[]), DataType.Binary },
                { typeof(object), DataType.Variant }
            };

        [IntelliSense("Creates a Data Column of suitable type for each column in the source query. Only works for OLE DB partition sources.")]
        public void RefreshDataColumns()
        {
            if (Partitions.Count == 0 || !(Partitions[0].DataSource is ProviderDataSource) || string.IsNullOrEmpty(Partitions[0].Query))
                throw new InvalidOperationException("The first partition on this table must use a ProviderDataSource with a valid OLE DB query.");

            try
            {
                using (var conn = new OleDbConnection((Partitions[0].DataSource as ProviderDataSource).ConnectionString))
                {
                    conn.Open();

                    var cmd = new OleDbCommand(Partitions[0].Query, conn);
                    var rdr = cmd.ExecuteReader(CommandBehavior.SchemaOnly);
                    var schema = rdr.GetSchemaTable();

                    foreach(DataRow row in schema.Rows)
                    {
                        var name = (string)row["ColumnName"];
                        var type = (Type)row["DataType"];
                        
                        if(!Columns.Contains(name))
                        {
                            var col = AddDataColumn(name, name);
                            col.DataType = DataTypeMapping.ContainsKey(type) ? DataTypeMapping[type] : DataType.Automatic;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Unable to generate metadata from partition source query: " + ex.Message);
            }
        }

        [Category("Metadata"),DisplayName("Error Message")]
        public virtual string ErrorMessage { get; protected set; }

        internal virtual void CheckChildrenErrors()
        {
            var errObj = GetChildren().OfType<IErrorMessageObject>().FirstOrDefault(c => !string.IsNullOrEmpty(c.ErrorMessage));
            if (errObj != null && (!(errObj as IExpressionObject)?.NeedsValidation ?? true))
            {
                ErrorMessage = "Error on " + (errObj as TabularNamedObject).Name + ": " + errObj.ErrorMessage;
            }
            else
            {
                ErrorMessage = null;
            }
        }

        protected override void Children_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (sender is PartitionCollection)
            {
                // When a partition is added / removed, we notify the logical tree about changes to the
                // PartitionViewTable, which is the object that holds the Partitions in the logical tree.
                if (e.Action == NotifyCollectionChangedAction.Add)
                    Handler.Tree.OnNodesInserted(PartitionViewTable, e.NewItems.Cast<ITabularObject>());
                else if (e.Action == NotifyCollectionChangedAction.Remove)
                    Handler.Tree.OnNodesRemoved(PartitionViewTable, e.OldItems.Cast<ITabularObject>());
            }
            else
            {
                // All other objects are shown as children of the table in the logical tree, so just
                // notify in the normal way:
                base.Children_CollectionChanged(sender, e);
            }
        }

        public override string Name
        {
            set
            {
                if (value.IndexOfAny(InvalidTableNameChars) != -1) throw new ArgumentException("Table name cannot contain any of the following characters: " + string.Join(" ", InvalidTableNameChars));
                base.Name = value;
            }
            get { return base.Name; }
        }

        protected override void OnPropertyChanged(string propertyName, object oldValue, object newValue)
        {
            if (propertyName == Properties.NAME)
            {
                if (Handler.Settings.AutoFixup)
                {
                    FormulaFixup.DoFixup(this);
                    Handler.UndoManager.EndBatch();
                }
                Handler.Tree.FolderCache.Clear(); // Clear folder cache when a table is renamed.

                // Update relationship "names" if this table participates in any relationships:
                var rels = UsedInRelationships.ToList();
                if (rels.Count > 1) Handler.Tree.BeginUpdate();
                rels.ForEach(r => r.UpdateName());
                if (rels.Count > 1) Handler.Tree.EndUpdate();
            }
            if (propertyName == Properties.DEFAULTDETAILROWSEXPRESSION)
            {
                FormulaFixup.BuildDependencyTree(this);
            }

            base.OnPropertyChanged(propertyName, oldValue, newValue);
        }
        protected override void OnPropertyChanging(string propertyName, object newValue, ref bool undoable, ref bool cancel)
        {
            if (propertyName == Properties.NAME)
            {
                // TODO: Important!!!
                // - Dependency Tree will be built once for every table that's had its name changed. This can be slow if many tables are renamed at once.
                // - We don't need a full rebuild of the dependency tree. We can limit ourselves to those expressions that contain a token matching the new name of this table.
                // - Also note that we should apply the fix-up before the tree is rebuilt.
                FormulaFixup.BuildDependencyTree();

                // When formula fixup is enabled, we need to begin a new batch of undo operations, as this
                // name change could result in expression changes on multiple objects:
                if (Handler.Settings.AutoFixup) Handler.UndoManager.BeginBatch("Set Property 'Name'");
            }
            base.OnPropertyChanging(propertyName, newValue, ref undoable, ref cancel);
        }

        public static readonly char[] InvalidTableNameChars = {
            '.', ',', ';', '\'', '`', ':', '/', '\\', '*', '|',
            '?', '"', '&', '%', '$' , '!', '+', '=', '(', ')',
            '[', ']', '{', '}', '<', '>'
        };

        [DisplayName("Default Detail Rows Expression")]
        [Category("Options"), IntelliSense("A DAX expression specifying default detail rows for this table (drill-through in client tools).")]
        [Editor(typeof(System.ComponentModel.Design.MultilineStringEditor), typeof(System.Drawing.Design.UITypeEditor))]
        public string DefaultDetailRowsExpression
        {
            get
            {
                return MetadataObject.DefaultDetailRowsDefinition?.Expression;
            }
            set
            {
                var oldValue = DefaultDetailRowsExpression;

                if (oldValue == value) return;

                bool undoable = true;
                bool cancel = false;
                OnPropertyChanging(Properties.DEFAULTDETAILROWSEXPRESSION, value, ref undoable, ref cancel);
                if (cancel) return;

                if (MetadataObject.DefaultDetailRowsDefinition == null) MetadataObject.DefaultDetailRowsDefinition = new TOM.DetailRowsDefinition();
                MetadataObject.DefaultDetailRowsDefinition.Expression = value;
                if (string.IsNullOrWhiteSpace(value)) MetadataObject.DefaultDetailRowsDefinition = null;

                if (undoable) Handler.UndoManager.Add(new UndoPropertyChangedAction(this, Properties.DEFAULTDETAILROWSEXPRESSION, oldValue, value));
                OnPropertyChanged(Properties.DEFAULTDETAILROWSEXPRESSION, oldValue, value);
            }
        }

        [Browsable(false)]
        public virtual bool NeedsValidation
        {
            get
            {
                return false;
            }

            set
            {
                
            }
        }

        [Browsable(false)]
        public virtual string Expression
        {
            get
            {
                return DefaultDetailRowsExpression;
            }

            set
            {
                DefaultDetailRowsExpression = value;
            }
        }
    }

    /// <summary>
    /// Wrapper class for the "Table Partitions" logical group.
    /// </summary>
	[TypeConverter(typeof(DynamicPropertyConverter))]
    public class PartitionViewTable: ITabularNamedObject, ITabularObjectContainer, IDynamicPropertyObject
    {
        public Table Table { get; private set; }
        internal PartitionViewTable(Table table)
        {
            Table = table;
        }

        [Category("Partitions"), NoMultiselect()]
        [Editor(typeof(PartitionCollectionEditor), typeof(UITypeEditor))]
        public PartitionCollection Partitions { get { return Table.Partitions; } }

        [Category("Partitions"), DisplayName("Table Name")]
        public string Name
        {
            get { return Table.Name; }
            set { Table.Name = value; }
        }

        public int MetadataIndex => Table.MetadataIndex;

        public ObjectType ObjectType => ObjectType.Table;

        public Model Model => Table.Model;

        public event PropertyChangedEventHandler PropertyChanged;

        public IEnumerable<ITabularNamedObject> GetChildren()
        {
            return Partitions;
        }

        public bool Browsable(string propertyName)
        {
            switch(propertyName)
            {
                case Properties.PARTITIONS:
                    return !(Table is CalculatedTable);
                case Properties.NAME:
                    return true;
            }
            return false;
        }

        public bool Editable(string propertyName)
        {
            return propertyName == Properties.NAME;
        }

        public bool CanDelete()
        {
            return Table.CanDelete();
        }

        public bool CanDelete(out string message)
        {
            return Table.CanDelete(out message);
        }

        public void Delete()
        {
            Table.Delete();
        }
    }

    internal static partial class Properties
    {
        public const string DEFAULTDETAILROWSEXPRESSION = "DefaultDetailRowsExpression";
    }

    internal static class TableExtension
    {
        public static TOM.PartitionSourceType GetSourceType(this TOM.Table table)
        {
            return table.Partitions.FirstOrDefault()?.SourceType ?? TOM.PartitionSourceType.None;
        }
    }
}
