namespace chris7197.DevDeck.Tray;

partial class PluginManagerForm
{
    private System.ComponentModel.IContainer? components = null;
    private System.Windows.Forms.SplitContainer splitter = null!;
    private System.Windows.Forms.ListView pluginList = null!;
    private System.Windows.Forms.ColumnHeader pluginColumn = null!;
    private System.Windows.Forms.ColumnHeader stateColumn = null!;
    private System.Windows.Forms.FlowLayoutPanel toolbar = null!;
    private System.Windows.Forms.Button btnBrowse = null!;
    private System.Windows.Forms.Button btnRefresh = null!;
    private System.Windows.Forms.Button btnOpenFolder = null!;
    private System.Windows.Forms.Label hostPath = null!;
    private System.Windows.Forms.TableLayoutPanel details = null!;
    private System.Windows.Forms.Label lblPluginName = null!;
    private System.Windows.Forms.TextBox lblStatus = null!;
    private System.Windows.Forms.Label lblInstructions = null!;
    private System.Windows.Forms.Panel fieldViewport = null!;
    private System.Windows.Forms.TableLayoutPanel fieldTable = null!;
    private System.Windows.Forms.FlowLayoutPanel bottom = null!;
    private System.Windows.Forms.Button btnSave = null!;
    private System.Windows.Forms.Button btnDiscard = null!;
    private System.Windows.Forms.Label lblChanged = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        splitter = new System.Windows.Forms.SplitContainer();
        pluginList = new System.Windows.Forms.ListView();
        pluginColumn = new System.Windows.Forms.ColumnHeader();
        stateColumn = new System.Windows.Forms.ColumnHeader();
        toolbar = new System.Windows.Forms.FlowLayoutPanel();
        btnBrowse = new System.Windows.Forms.Button();
        btnRefresh = new System.Windows.Forms.Button();
        btnOpenFolder = new System.Windows.Forms.Button();
        hostPath = new System.Windows.Forms.Label();
        details = new System.Windows.Forms.TableLayoutPanel();
        lblPluginName = new System.Windows.Forms.Label();
        lblStatus = new System.Windows.Forms.TextBox();
        lblInstructions = new System.Windows.Forms.Label();
        fieldViewport = new System.Windows.Forms.Panel();
        fieldTable = new System.Windows.Forms.TableLayoutPanel();
        bottom = new System.Windows.Forms.FlowLayoutPanel();
        btnSave = new System.Windows.Forms.Button();
        btnDiscard = new System.Windows.Forms.Button();
        lblChanged = new System.Windows.Forms.Label();
        ((System.ComponentModel.ISupportInitialize)splitter).BeginInit();
        splitter.Panel1.SuspendLayout();
        splitter.Panel2.SuspendLayout();
        splitter.SuspendLayout();
        toolbar.SuspendLayout();
        details.SuspendLayout();
        fieldViewport.SuspendLayout();
        bottom.SuspendLayout();
        SuspendLayout();
        // toolbar
        toolbar.Dock = System.Windows.Forms.DockStyle.Top;
        toolbar.Height = 76;
        toolbar.Padding = new System.Windows.Forms.Padding(12, 10, 0, 0);
        toolbar.WrapContents = true;
        toolbar.Controls.Add(btnBrowse);
        toolbar.Controls.Add(btnRefresh);
        toolbar.Controls.Add(btnOpenFolder);
        toolbar.Controls.Add(hostPath);
        btnBrowse.Text = "Select installation…";
        btnBrowse.AutoSize = true;
        btnBrowse.Click += btnBrowse_Click;
        btnRefresh.Text = "Refresh";
        btnRefresh.AutoSize = true;
        btnRefresh.Click += btnRefresh_Click;
        btnOpenFolder.Text = "Open plugin folder";
        btnOpenFolder.AutoSize = true;
        btnOpenFolder.Click += btnOpenFolder_Click;
        hostPath.AutoSize = false;
        hostPath.Width = 900;
        hostPath.Height = 26;
        hostPath.AutoEllipsis = true;
        hostPath.ForeColor = System.Drawing.SystemColors.GrayText;
        hostPath.Margin = new System.Windows.Forms.Padding(6, 5, 0, 0);
        // splitter
        splitter.Dock = System.Windows.Forms.DockStyle.Fill;
        splitter.SplitterDistance = 285;
        splitter.Panel1.Controls.Add(pluginList);
        splitter.Panel2.Controls.Add(details);
        pluginList.Dock = System.Windows.Forms.DockStyle.Fill;
        pluginList.View = System.Windows.Forms.View.Details;
        pluginList.FullRowSelect = true;
        pluginList.HideSelection = false;
        pluginList.MultiSelect = false;
        pluginList.Columns.AddRange([pluginColumn, stateColumn]);
        pluginColumn.Text = "Plugin";
        pluginColumn.Width = 175;
        stateColumn.Text = "State";
        stateColumn.Width = 100;
        pluginList.SelectedIndexChanged += pluginList_SelectedIndexChanged;
        // right column
        details.Dock = System.Windows.Forms.DockStyle.Fill;
        details.Padding = new System.Windows.Forms.Padding(12, 8, 12, 8);
        details.ColumnCount = 1;
        details.RowCount = 5;
        details.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100));
        details.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 36));
        details.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 90));
        details.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 40));
        details.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100));
        details.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 49));
        details.Controls.Add(lblPluginName, 0, 0);
        details.Controls.Add(lblStatus, 0, 1);
        details.Controls.Add(lblInstructions, 0, 2);
        details.Controls.Add(fieldViewport, 0, 3);
        details.Controls.Add(bottom, 0, 4);
        lblPluginName.Dock = System.Windows.Forms.DockStyle.Fill;
        lblPluginName.Font = new System.Drawing.Font("Segoe UI", 14F, System.Drawing.FontStyle.Bold);
        lblPluginName.Text = "Select a plugin";
        lblStatus.Dock = System.Windows.Forms.DockStyle.Fill;
        lblStatus.Multiline = true;
        lblStatus.ReadOnly = true;
        lblStatus.BorderStyle = System.Windows.Forms.BorderStyle.None;
        lblStatus.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
        lblStatus.BackColor = System.Drawing.SystemColors.Control;
        lblStatus.ForeColor = System.Drawing.SystemColors.GrayText;
        lblStatus.Text = "Startup status will appear here.";
        lblInstructions.Dock = System.Windows.Forms.DockStyle.Fill;
        lblInstructions.Text = "Edit settings below. Restart the Stream Dock host to apply changes.";
        lblInstructions.ForeColor = System.Drawing.SystemColors.GrayText;
        fieldViewport.AutoScroll = true;
        fieldViewport.Dock = System.Windows.Forms.DockStyle.Fill;
        fieldViewport.Controls.Add(fieldTable);
        fieldTable.Dock = System.Windows.Forms.DockStyle.Top;
        fieldTable.AutoSize = true;
        fieldTable.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
        fieldTable.ColumnCount = 2;
        fieldTable.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 205));
        fieldTable.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100));
        // bottom
        bottom.Dock = System.Windows.Forms.DockStyle.Fill;
        bottom.Controls.Add(btnSave);
        bottom.Controls.Add(btnDiscard);
        bottom.Controls.Add(lblChanged);
        btnSave.Text = "Save settings";
        btnSave.Width = 130;
        btnSave.Height = 33;
        btnSave.Enabled = false;
        btnSave.Click += btnSave_Click;
        btnDiscard.Text = "Discard edits";
        btnDiscard.Width = 130;
        btnDiscard.Height = 33;
        btnDiscard.Enabled = false;
        btnDiscard.Click += btnDiscard_Click;
        lblChanged.AutoSize = true;
        lblChanged.Padding = new System.Windows.Forms.Padding(6, 10, 0, 0);
        lblChanged.ForeColor = System.Drawing.SystemColors.GrayText;
        // form
        AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(1080, 675);
        MinimumSize = new System.Drawing.Size(850, 480);
        Controls.Add(splitter);
        Controls.Add(toolbar);
        StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
        Text = "DevDeck — Plugin manager";
        splitter.Panel1.ResumeLayout(false);
        splitter.Panel2.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)splitter).EndInit();
        splitter.ResumeLayout(false);
        toolbar.ResumeLayout(false);
        details.ResumeLayout(false);
        fieldViewport.ResumeLayout(false);
        bottom.ResumeLayout(false);
        ResumeLayout(false);
    }
}
