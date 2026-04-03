namespace ZKSWorkerGUI
{
    partial class Csv
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            button1 = new Button();
            button2 = new Button();
            button3 = new Button();
            button4 = new Button();
            button5 = new Button();
            button7 = new Button();
            button8 = new Button();
            textBox1 = new TextBox();
            comboBox1 = new ComboBox();
            comboBox2 = new ComboBox();
            comboBox3 = new ComboBox();
            labelUrl = new Label();
            textBoxUrl = new TextBox();
            dataGridView1 = new DataGridView();
            label1 = new Label();
            groupBox1 = new GroupBox();
            panel5 = new Panel();
            button9 = new Button();
            button6 = new Button();
            panel4 = new Panel();
            label2 = new Label();
            label6 = new Label();
            label5 = new Label();
            groupBox2 = new GroupBox();
            label4 = new Label();
            label3 = new Label();
            ((System.ComponentModel.ISupportInitialize)dataGridView1).BeginInit();
            groupBox1.SuspendLayout();
            panel5.SuspendLayout();
            panel4.SuspendLayout();
            groupBox2.SuspendLayout();
            SuspendLayout();
            // 
            // button1
            // 
            button1.Location = new Point(153, 26);
            button1.Name = "button1";
            button1.Size = new Size(115, 46);
            button1.TabIndex = 0;
            button1.Text = "下载更新";
            button1.UseVisualStyleBackColor = true;
            // 
            // button2
            // 
            button2.Location = new Point(454, 26);
            button2.Name = "button2";
            button2.Size = new Size(115, 46);
            button2.TabIndex = 1;
            button2.Text = "合并数据";
            button2.UseVisualStyleBackColor = true;
            // 
            // button3
            // 
            button3.Location = new Point(849, 86);
            button3.Name = "button3";
            button3.Size = new Size(115, 46);
            button3.TabIndex = 2;
            button3.Text = "自定义下载";
            button3.UseVisualStyleBackColor = true;
            // 
            // button4
            // 
            button4.Location = new Point(847, 3);
            button4.Name = "button4";
            button4.Size = new Size(116, 50);
            button4.TabIndex = 15;
            button4.Text = "重新加载";
            button4.UseVisualStyleBackColor = true;
            // 
            // button5
            // 
            button5.Location = new Point(728, 8);
            button5.Name = "button5";
            button5.Size = new Size(116, 50);
            button5.TabIndex = 16;
            button5.Text = "删除选中行";
            button5.UseVisualStyleBackColor = true;
            // 
            // button7
            // 
            button7.Location = new Point(847, 8);
            button7.Name = "button7";
            button7.Size = new Size(116, 50);
            button7.TabIndex = 18;
            button7.Text = "清空全部";
            button7.UseVisualStyleBackColor = true;
            // 
            // button8
            // 
            button8.Location = new Point(728, 3);
            button8.Name = "button8";
            button8.Size = new Size(116, 50);
            button8.TabIndex = 22;
            button8.Text = "删除重复行";
            button8.UseVisualStyleBackColor = true;
            // 
            // textBox1
            // 
            textBox1.Location = new Point(355, 15);
            textBox1.Name = "textBox1";
            textBox1.Size = new Size(259, 30);
            textBox1.TabIndex = 3;
            // 
            // comboBox1
            // 
            comboBox1.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBox1.FormattingEnabled = true;
            comboBox1.Location = new Point(100, 14);
            comboBox1.Name = "comboBox1";
            comboBox1.Size = new Size(125, 32);
            comboBox1.TabIndex = 4;
            // 
            // comboBox2
            // 
            comboBox2.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBox2.FormattingEnabled = true;
            comboBox2.Location = new Point(227, 14);
            comboBox2.Name = "comboBox2";
            comboBox2.Size = new Size(125, 32);
            comboBox2.TabIndex = 5;
            // 
            // comboBox3
            // 
            comboBox3.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBox3.FormattingEnabled = true;
            comboBox3.Items.AddRange(new object[] { "追加模式(去重)", "覆盖模式(清空后写入)" });
            comboBox3.Location = new Point(761, 33);
            comboBox3.Name = "comboBox3";
            comboBox3.Size = new Size(203, 32);
            comboBox3.TabIndex = 21;
            // 
            // labelUrl
            // 
            labelUrl.AutoSize = true;
            labelUrl.Location = new Point(9, 97);
            labelUrl.Name = "labelUrl";
            labelUrl.Size = new Size(140, 24);
            labelUrl.TabIndex = 13;
            labelUrl.Text = "自定义链接下载:";
            // 
            // textBoxUrl
            // 
            textBoxUrl.Location = new Point(153, 94);
            textBoxUrl.Name = "textBoxUrl";
            textBoxUrl.Size = new Size(677, 30);
            textBoxUrl.TabIndex = 14;
            // 
            // dataGridView1
            // 
            dataGridView1.AllowUserToAddRows = false;
            dataGridView1.AllowUserToDeleteRows = false;
            dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            dataGridView1.BackgroundColor = SystemColors.Control;
            dataGridView1.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dataGridView1.Location = new Point(1, 102);
            dataGridView1.Name = "dataGridView1";
            dataGridView1.ReadOnly = true;
            dataGridView1.RowHeadersWidth = 30;
            dataGridView1.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dataGridView1.Size = new Size(992, 734);
            dataGridView1.TabIndex = 8;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.BackColor = SystemColors.ControlLight;
            label1.Location = new Point(9, 21);
            label1.Name = "label1";
            label1.Size = new Size(103, 24);
            label1.TabIndex = 9;
            label1.Text = "共 0 条记录";
            // 
            // groupBox1
            // 
            groupBox1.Controls.Add(dataGridView1);
            groupBox1.Controls.Add(panel5);
            groupBox1.Controls.Add(panel4);
            groupBox1.Location = new Point(13, 150);
            groupBox1.Name = "groupBox1";
            groupBox1.Size = new Size(993, 904);
            groupBox1.TabIndex = 11;
            groupBox1.TabStop = false;
            groupBox1.Text = "数据列表";
            // 
            // panel5
            // 
            panel5.BackColor = SystemColors.ControlLight;
            panel5.BorderStyle = BorderStyle.FixedSingle;
            panel5.Controls.Add(button9);
            panel5.Controls.Add(button6);
            panel5.Controls.Add(label1);
            panel5.Controls.Add(button5);
            panel5.Controls.Add(button7);
            panel5.Location = new Point(0, 833);
            panel5.Name = "panel5";
            panel5.Size = new Size(993, 68);
            panel5.TabIndex = 19;
            // 
            // button9
            // 
            button9.Location = new Point(581, 8);
            button9.Name = "button9";
            button9.Size = new Size(116, 50);
            button9.TabIndex = 24;
            button9.Text = "下一页";
            button9.UseVisualStyleBackColor = true;
            // 
            // button6
            // 
            button6.Location = new Point(460, 8);
            button6.Name = "button6";
            button6.Size = new Size(116, 50);
            button6.TabIndex = 23;
            button6.Text = "上一页";
            button6.UseVisualStyleBackColor = true;
            // 
            // panel4
            // 
            panel4.BackColor = SystemColors.ControlLight;
            panel4.BorderStyle = BorderStyle.FixedSingle;
            panel4.Controls.Add(label2);
            panel4.Controls.Add(comboBox1);
            panel4.Controls.Add(button4);
            panel4.Controls.Add(button8);
            panel4.Controls.Add(textBox1);
            panel4.Controls.Add(comboBox2);
            panel4.Location = new Point(1, 42);
            panel4.Name = "panel4";
            panel4.Size = new Size(992, 60);
            panel4.TabIndex = 11;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            label2.Location = new Point(9, 16);
            label2.Name = "label2";
            label2.Size = new Size(86, 24);
            label2.TabIndex = 10;
            label2.Text = "数据筛选:";
            // 
            // label6
            // 
            label6.AutoSize = true;
            label6.Font = new Font("Microsoft YaHei UI", 9F);
            label6.ForeColor = Color.Brown;
            label6.Location = new Point(24, 1057);
            label6.Name = "label6";
            label6.Size = new Size(557, 24);
            label6.TabIndex = 13;
            label6.Text = "温馨提示：点上面列表对应行的第一个空格就是复制所需的 IP:Port。\r\n";
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Location = new Point(597, 37);
            label5.Name = "label5";
            label5.Size = new Size(158, 24);
            label5.TabIndex = 20;
            label5.Text = "写入数据库的模式:";
            // 
            // groupBox2
            // 
            groupBox2.Controls.Add(label5);
            groupBox2.Controls.Add(button3);
            groupBox2.Controls.Add(label4);
            groupBox2.Controls.Add(label3);
            groupBox2.Controls.Add(comboBox3);
            groupBox2.Controls.Add(textBoxUrl);
            groupBox2.Controls.Add(labelUrl);
            groupBox2.Controls.Add(button1);
            groupBox2.Controls.Add(button2);
            groupBox2.Location = new Point(14, 11);
            groupBox2.Name = "groupBox2";
            groupBox2.Size = new Size(992, 151);
            groupBox2.TabIndex = 12;
            groupBox2.TabStop = false;
            groupBox2.Text = "数据源";
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new Point(286, 37);
            label4.Name = "label4";
            label4.Size = new Size(156, 24);
            label4.TabIndex = 17;
            label4.Text = "导入本地CSV数据:";
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Location = new Point(9, 37);
            label3.Name = "label3";
            label3.Size = new Size(140, 24);
            label3.TabIndex = 16;
            label3.Text = "使用内置数据源:";
            // 
            // Csv
            // 
            AutoScaleDimensions = new SizeF(11F, 24F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1020, 1090);
            Controls.Add(label6);
            Controls.Add(groupBox1);
            Controls.Add(groupBox2);
            Name = "Csv";
            RightToLeftLayout = true;
            Text = "ProxyIP管理";
            ((System.ComponentModel.ISupportInitialize)dataGridView1).EndInit();
            groupBox1.ResumeLayout(false);
            panel5.ResumeLayout(false);
            panel5.PerformLayout();
            panel4.ResumeLayout(false);
            panel4.PerformLayout();
            groupBox2.ResumeLayout(false);
            groupBox2.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button button1;
        private Button button2;
        private Button button3;
        private Button button4;
        private Button button5;
        private Button button7;
        private Button button8;
        private TextBox textBox1;
        private ComboBox comboBox1;
        private ComboBox comboBox2;
        private ComboBox comboBox3;
        private DataGridView dataGridView1;
        private Label label1;
        private GroupBox groupBox1;
        private GroupBox groupBox2;
        private Label label2;
        private Label labelUrl;
        private TextBox textBoxUrl;
        private Panel panel4;
        private Panel panel5;
        private Label label3;
        private Label label4;
        private Label label5;
        private Label label6;
        private Button button9;
        private Button button6;
    }
}
