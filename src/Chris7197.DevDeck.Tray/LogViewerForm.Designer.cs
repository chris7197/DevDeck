namespace chris7197.DevDeck.Tray;

partial class LogViewerForm
{
    private System.ComponentModel.IContainer? components = null;
    private System.Windows.Forms.FlowLayoutPanel toolbar = null!;
    private System.Windows.Forms.CheckBox chkErrorsOnly = null!;
    private System.Windows.Forms.Button btnOpenLog = null!;
    private System.Windows.Forms.Button btnCopyDetails = null!;
    private System.Windows.Forms.Button btnClearLog = null!;
    private System.Windows.Forms.Label lblSummary = null!;
    private System.Windows.Forms.SplitContainer splitContainer = null!;
    private System.Windows.Forms.ListView lvEntries = null!;
    private System.Windows.Forms.ColumnHeader colTime = null!;
    private System.Windows.Forms.ColumnHeader colLevel = null!;
    private System.Windows.Forms.ColumnHeader colEntry = null!;
    private System.Windows.Forms.TextBox txtDetails = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        toolbar = new System.Windows.Forms.FlowLayoutPanel();
        chkErrorsOnly = new System.Windows.Forms.CheckBox();
        btnOpenLog = new System.Windows.Forms.Button();
        btnCopyDetails = new System.Windows.Forms.Button();
        btnClearLog = new System.Windows.Forms.Button();
        lblSummary = new System.Windows.Forms.Label();
        splitContainer = new System.Windows.Forms.SplitContainer();
        lvEntries = new System.Windows.Forms.ListView();
        colTime = new System.Windows.Forms.ColumnHeader();
        colLevel = new System.Windows.Forms.ColumnHeader();
        colEntry = new System.Windows.Forms.ColumnHeader();
        txtDetails = new System.Windows.Forms.TextBox();
        toolbar.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)splitContainer).BeginInit();
        splitContainer.Panel1.SuspendLayout();
        splitContainer.Panel2.SuspendLayout();
        splitContainer.SuspendLayout();
        SuspendLayout();
        //
        // toolbar
        //
        toolbar.AutoSize = true;
        toolbar.BackColor = System.Drawing.Color.FromArgb(24, 30, 42);
        toolbar.Controls.Add(chkErrorsOnly);
        toolbar.Controls.Add(btnOpenLog);
        toolbar.Controls.Add(btnCopyDetails);
        toolbar.Controls.Add(btnClearLog);
        toolbar.Controls.Add(lblSummary);
        toolbar.Dock = System.Windows.Forms.DockStyle.Top;
        toolbar.Location = new System.Drawing.Point(0, 0);
        toolbar.Name = "toolbar";
        toolbar.Padding = new System.Windows.Forms.Padding(10, 9, 10, 5);
        toolbar.Size = new System.Drawing.Size(920, 47);
        toolbar.TabIndex = 0;
        toolbar.WrapContents = true;
        //
        // chkErrorsOnly
        //
        chkErrorsOnly.AutoSize = true;
        chkErrorsOnly.ForeColor = System.Drawing.Color.White;
        chkErrorsOnly.Location = new System.Drawing.Point(13, 15);
        chkErrorsOnly.Margin = new System.Windows.Forms.Padding(3, 6, 8, 3);
        chkErrorsOnly.Name = "chkErrorsOnly";
        chkErrorsOnly.Size = new System.Drawing.Size(166, 21);
        chkErrorsOnly.TabIndex = 0;
        chkErrorsOnly.Text = "Errors / warnings only";
        chkErrorsOnly.UseVisualStyleBackColor = true;
        chkErrorsOnly.CheckedChanged += chkErrorsOnly_CheckedChanged;
        //
        // btnOpenLog
        //
        btnOpenLog.AutoSize = true;
        btnOpenLog.Location = new System.Drawing.Point(190, 9);
        btnOpenLog.Name = "btnOpenLog";
        btnOpenLog.Size = new System.Drawing.Size(109, 30);
        btnOpenLog.TabIndex = 1;
        btnOpenLog.Text = "Open log file";
        btnOpenLog.UseVisualStyleBackColor = true;
        btnOpenLog.Click += btnOpenLog_Click;
        //
        // btnCopyDetails
        //
        btnCopyDetails.AutoSize = true;
        btnCopyDetails.Location = new System.Drawing.Point(305, 9);
        btnCopyDetails.Name = "btnCopyDetails";
        btnCopyDetails.Size = new System.Drawing.Size(112, 30);
        btnCopyDetails.TabIndex = 2;
        btnCopyDetails.Text = "Copy details";
        btnCopyDetails.UseVisualStyleBackColor = true;
        btnCopyDetails.Click += btnCopyDetails_Click;
        //
        // btnClearLog
        //
        btnClearLog.AutoSize = true;
        btnClearLog.Location = new System.Drawing.Point(423, 9);
        btnClearLog.Name = "btnClearLog";
        btnClearLog.Size = new System.Drawing.Size(92, 30);
        btnClearLog.TabIndex = 3;
        btnClearLog.Text = "Clear log";
        btnClearLog.UseVisualStyleBackColor = true;
        btnClearLog.Click += btnClearLog_Click;
        //
        // lblSummary
        //
        lblSummary.AutoSize = true;
        lblSummary.ForeColor = System.Drawing.Color.FromArgb(185, 197, 218);
        lblSummary.Location = new System.Drawing.Point(530, 17);
        lblSummary.Margin = new System.Windows.Forms.Padding(12, 8, 0, 0);
        lblSummary.Name = "lblSummary";
        lblSummary.Size = new System.Drawing.Size(94, 17);
        lblSummary.TabIndex = 4;
        lblSummary.Text = "0 recent entries";
        //
        // splitContainer
        //
        splitContainer.Dock = System.Windows.Forms.DockStyle.Fill;
        splitContainer.Location = new System.Drawing.Point(0, 47);
        splitContainer.Name = "splitContainer";
        splitContainer.Orientation = System.Windows.Forms.Orientation.Horizontal;
        splitContainer.Panel1.Controls.Add(lvEntries);
        splitContainer.Panel2.Controls.Add(txtDetails);
        splitContainer.Size = new System.Drawing.Size(920, 573);
        splitContainer.SplitterDistance = 275;
        splitContainer.TabIndex = 1;
        //
        // lvEntries
        //
        lvEntries.BackColor = System.Drawing.Color.FromArgb(32, 40, 55);
        lvEntries.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] { colTime, colLevel, colEntry });
        lvEntries.Dock = System.Windows.Forms.DockStyle.Fill;
        lvEntries.ForeColor = System.Drawing.Color.White;
        lvEntries.FullRowSelect = true;
        lvEntries.HideSelection = false;
        lvEntries.Location = new System.Drawing.Point(0, 0);
        lvEntries.MultiSelect = false;
        lvEntries.Name = "lvEntries";
        lvEntries.Size = new System.Drawing.Size(920, 275);
        lvEntries.TabIndex = 0;
        lvEntries.UseCompatibleStateImageBehavior = false;
        lvEntries.View = System.Windows.Forms.View.Details;
        lvEntries.SelectedIndexChanged += lvEntries_SelectedIndexChanged;
        lvEntries.DoubleClick += lvEntries_DoubleClick;
        //
        // colTime
        //
        colTime.Text = "Time";
        colTime.Width = 155;
        //
        // colLevel
        //
        colLevel.Text = "Level";
        colLevel.Width = 90;
        //
        // colEntry
        //
        colEntry.Text = "Entry";
        colEntry.Width = 600;
        //
        // txtDetails
        //
        txtDetails.BackColor = System.Drawing.Color.FromArgb(18, 23, 32);
        txtDetails.Dock = System.Windows.Forms.DockStyle.Fill;
        txtDetails.Font = new System.Drawing.Font("Consolas", 10F);
        txtDetails.ForeColor = System.Drawing.Color.FromArgb(227, 234, 246);
        txtDetails.Location = new System.Drawing.Point(0, 0);
        txtDetails.Multiline = true;
        txtDetails.Name = "txtDetails";
        txtDetails.ReadOnly = true;
        txtDetails.ScrollBars = System.Windows.Forms.ScrollBars.Both;
        txtDetails.ShortcutsEnabled = true;
        txtDetails.Size = new System.Drawing.Size(920, 294);
        txtDetails.TabIndex = 0;
        txtDetails.WordWrap = false;
        //
        // LogViewerForm
        //
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        BackColor = System.Drawing.Color.FromArgb(24, 30, 42);
        ClientSize = new System.Drawing.Size(920, 620);
        Controls.Add(splitContainer);
        Controls.Add(toolbar);
        Font = new System.Drawing.Font("Segoe UI", 10F);
        ForeColor = System.Drawing.Color.White;
        MinimumSize = new System.Drawing.Size(590, 370);
        Name = "LogViewerForm";
        ShowInTaskbar = true;
        StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
        Text = "DevDeck — Activity Log";
        toolbar.ResumeLayout(false);
        toolbar.PerformLayout();
        splitContainer.Panel1.ResumeLayout(false);
        splitContainer.Panel2.ResumeLayout(false);
        splitContainer.Panel2.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)splitContainer).EndInit();
        splitContainer.ResumeLayout(false);
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion
}
