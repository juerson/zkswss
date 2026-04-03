namespace ZKSWorkerGUI
{
    partial class Cidrs
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            groupBox1 = new GroupBox();
            btnCustomDown = new Button();
            btnMergeCidrs = new Button();
            label1 = new Label();
            comboBoxMode = new ComboBox();
            groupBox2 = new GroupBox();
            textBoxCustomUrl = new TextBox();
            btnUrlDown = new Button();
            groupBox3 = new GroupBox();
            label2 = new Label();
            textBoxFilter = new TextBox();
            btnClearAll = new Button();
            btnDeduplicate = new Button();
            panel1 = new Panel();
            labelPageInfo2 = new Label();
            btnPrev = new Button();
            labelPageInfo1 = new Label();
            btnNext = new Button();
            btnCheckedDelete = new Button();
            dataGridView1 = new DataGridView();
            sqliteCommand1 = new Microsoft.Data.Sqlite.SqliteCommand();
            groupBox1.SuspendLayout();
            groupBox2.SuspendLayout();
            groupBox3.SuspendLayout();
            panel1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)dataGridView1).BeginInit();
            SuspendLayout();
            // 
            // groupBox1
            // 
            groupBox1.Controls.Add(btnCustomDown);
            groupBox1.Controls.Add(btnMergeCidrs);
            groupBox1.Controls.Add(label1);
            groupBox1.Controls.Add(comboBoxMode);
            groupBox1.Location = new Point(23, 12);
            groupBox1.Name = "groupBox1";
            groupBox1.Size = new Size(765, 100);
            groupBox1.TabIndex = 0;
            groupBox1.TabStop = false;
            groupBox1.Text = "数据获取与设置";
            // 
            // btnCustomDown
            // 
            btnCustomDown.Location = new Point(29, 39);
            btnCustomDown.Name = "btnCustomDown";
            btnCustomDown.Size = new Size(136, 45);
            btnCustomDown.TabIndex = 1;
            btnCustomDown.Text = "内置源下载";
            btnCustomDown.UseVisualStyleBackColor = true;
            // 
            // btnMergeCidrs
            // 
            btnMergeCidrs.Location = new Point(188, 39);
            btnMergeCidrs.Name = "btnMergeCidrs";
            btnMergeCidrs.Size = new Size(136, 45);
            btnMergeCidrs.TabIndex = 2;
            btnMergeCidrs.Text = "导入本地文件";
            btnMergeCidrs.UseVisualStyleBackColor = true;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(466, 49);
            label1.Name = "label1";
            label1.Size = new Size(91, 24);
            label1.TabIndex = 1;
            label1.Text = "写入模式: ";
            // 
            // comboBoxMode
            // 
            comboBoxMode.FormattingEnabled = true;
            comboBoxMode.Location = new Point(563, 46);
            comboBoxMode.Name = "comboBoxMode";
            comboBoxMode.Size = new Size(182, 32);
            comboBoxMode.TabIndex = 0;
            // 
            // groupBox2
            // 
            groupBox2.Controls.Add(textBoxCustomUrl);
            groupBox2.Controls.Add(btnUrlDown);
            groupBox2.Location = new Point(23, 123);
            groupBox2.Name = "groupBox2";
            groupBox2.Size = new Size(765, 90);
            groupBox2.TabIndex = 1;
            groupBox2.TabStop = false;
            groupBox2.Text = "自定义下载源";
            // 
            // textBoxCustomUrl
            // 
            textBoxCustomUrl.Location = new Point(29, 36);
            textBoxCustomUrl.Name = "textBoxCustomUrl";
            textBoxCustomUrl.Size = new Size(560, 30);
            textBoxCustomUrl.TabIndex = 0;
            // 
            // btnUrlDown
            // 
            btnUrlDown.Location = new Point(609, 29);
            btnUrlDown.Name = "btnUrlDown";
            btnUrlDown.Size = new Size(136, 45);
            btnUrlDown.TabIndex = 3;
            btnUrlDown.Text = "下载";
            btnUrlDown.UseVisualStyleBackColor = true;
            // 
            // groupBox3
            // 
            groupBox3.Controls.Add(label2);
            groupBox3.Controls.Add(textBoxFilter);
            groupBox3.Controls.Add(btnClearAll);
            groupBox3.Controls.Add(btnDeduplicate);
            groupBox3.Controls.Add(panel1);
            groupBox3.Controls.Add(btnCheckedDelete);
            groupBox3.Controls.Add(dataGridView1);
            groupBox3.Location = new Point(23, 224);
            groupBox3.Name = "groupBox3";
            groupBox3.Size = new Size(765, 728);
            groupBox3.TabIndex = 2;
            groupBox3.TabStop = false;
            groupBox3.Text = "数据列表";
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            label2.Location = new Point(6, 39);
            label2.Name = "label2";
            label2.Size = new Size(86, 24);
            label2.TabIndex = 12;
            label2.Text = "数据筛选:";
            // 
            // textBoxFilter
            // 
            textBoxFilter.Location = new Point(100, 36);
            textBoxFilter.Name = "textBoxFilter";
            textBoxFilter.Size = new Size(341, 30);
            textBoxFilter.TabIndex = 11;
            // 
            // btnClearAll
            // 
            btnClearAll.Location = new Point(629, 672);
            btnClearAll.Name = "btnClearAll";
            btnClearAll.Size = new Size(116, 45);
            btnClearAll.TabIndex = 9;
            btnClearAll.Text = "清空全部";
            btnClearAll.UseVisualStyleBackColor = true;
            btnClearAll.Click += btnClearAll_Click;
            // 
            // btnDeduplicate
            // 
            btnDeduplicate.Location = new Point(629, 28);
            btnDeduplicate.Name = "btnDeduplicate";
            btnDeduplicate.Size = new Size(116, 45);
            btnDeduplicate.TabIndex = 2;
            btnDeduplicate.Text = "一键去重";
            btnDeduplicate.UseVisualStyleBackColor = true;
            // 
            // panel1
            // 
            panel1.Controls.Add(labelPageInfo2);
            panel1.Controls.Add(btnPrev);
            panel1.Controls.Add(labelPageInfo1);
            panel1.Controls.Add(btnNext);
            panel1.Location = new Point(6, 672);
            panel1.Name = "panel1";
            panel1.Size = new Size(461, 43);
            panel1.TabIndex = 8;
            // 
            // labelPageInfo2
            // 
            labelPageInfo2.Location = new Point(227, 9);
            labelPageInfo2.Name = "labelPageInfo2";
            labelPageInfo2.Size = new Size(218, 25);
            labelPageInfo2.TabIndex = 7;
            labelPageInfo2.Text = "条目：[100/10000]";
            labelPageInfo2.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // btnPrev
            // 
            btnPrev.Location = new Point(3, 3);
            btnPrev.Name = "btnPrev";
            btnPrev.Size = new Size(46, 34);
            btnPrev.TabIndex = 3;
            btnPrev.Text = "<";
            btnPrev.UseVisualStyleBackColor = true;
            // 
            // labelPageInfo1
            // 
            labelPageInfo1.Location = new Point(52, 8);
            labelPageInfo1.Name = "labelPageInfo1";
            labelPageInfo1.Size = new Size(120, 25);
            labelPageInfo1.TabIndex = 5;
            labelPageInfo1.Text = "[100/100]";
            labelPageInfo1.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // btnNext
            // 
            btnNext.Location = new Point(175, 3);
            btnNext.Name = "btnNext";
            btnNext.Size = new Size(46, 34);
            btnNext.TabIndex = 4;
            btnNext.Text = ">";
            btnNext.UseVisualStyleBackColor = true;
            // 
            // btnCheckedDelete
            // 
            btnCheckedDelete.Location = new Point(507, 672);
            btnCheckedDelete.Name = "btnCheckedDelete";
            btnCheckedDelete.Size = new Size(116, 45);
            btnCheckedDelete.TabIndex = 1;
            btnCheckedDelete.Text = "选中删除";
            btnCheckedDelete.UseVisualStyleBackColor = true;
            // 
            // dataGridView1
            // 
            dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dataGridView1.BackgroundColor = SystemColors.Control;
            dataGridView1.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dataGridView1.Location = new Point(0, 82);
            dataGridView1.Name = "dataGridView1";
            dataGridView1.RowHeadersWidth = 62;
            dataGridView1.Size = new Size(765, 584);
            dataGridView1.TabIndex = 0;
            // 
            // sqliteCommand1
            // 
            sqliteCommand1.CommandTimeout = 30;
            sqliteCommand1.Connection = null;
            sqliteCommand1.Transaction = null;
            sqliteCommand1.UpdatedRowSource = System.Data.UpdateRowSource.None;
            // 
            // Cidrs
            // 
            AutoScaleDimensions = new SizeF(11F, 24F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(809, 964);
            Controls.Add(groupBox3);
            Controls.Add(groupBox2);
            Controls.Add(groupBox1);
            Name = "Cidrs";
            Text = "优选地址管理";
            groupBox1.ResumeLayout(false);
            groupBox1.PerformLayout();
            groupBox2.ResumeLayout(false);
            groupBox2.PerformLayout();
            groupBox3.ResumeLayout(false);
            groupBox3.PerformLayout();
            panel1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)dataGridView1).EndInit();
            ResumeLayout(false);
        }

        #endregion

        private GroupBox groupBox1;
        private GroupBox groupBox2;
        private Label label1;
        private ComboBox comboBoxMode;
        private Button btnCustomDown;
        private TextBox textBoxCustomUrl;
        private GroupBox groupBox3;
        private Button btnMergeCidrs;
        private Button btnUrlDown;
        private DataGridView dataGridView1;
        private Label labelPageInfo1;
        private Button btnNext;
        private Button btnPrev;
        private Button btnDeduplicate;
        private Button btnCheckedDelete;
        private Label labelPageInfo2;
        private Microsoft.Data.Sqlite.SqliteCommand sqliteCommand1;
        private Panel panel1;
        private Button btnClearAll;
        private Label label2;
        private TextBox textBoxFilter;
    }
}