using System.Diagnostics;
using System.Text;

namespace ZKSWorkerGUI
{
    partial class App
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;
        /// <summary>
        ///  Clean up any resources being used.
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
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            textBoxServer = new TextBox();
            label2 = new Label();
            buttonStart = new Button();
            process1 = new Process();
            textBoxLog = new TextBox();
            notifyIcon1 = new NotifyIcon(components);
            comboBoxServics = new ComboBox();
            buttonSave = new Button();
            label4 = new Label();
            textBoxListen = new TextBox();
            label7 = new Label();
            textBoxIp = new TextBox();
            textBoxToken = new TextBox();
            label8 = new Label();
            buttonAdd = new Button();
            buttonRename = new Button();
            buttonDel = new Button();
            groupBox1 = new GroupBox();
            groupBox2 = new GroupBox();
            linkLabelCidrsForm = new LinkLabel();
            linkLabelCsvForm = new LinkLabel();
            textBoxFallback = new TextBox();
            groupBox3 = new GroupBox();
            checkBoxAutoStartHide = new CheckBox();
            buttonStop = new Button();
            checkBoxAutoStart = new CheckBox();
            checkBoxSystemProxy = new CheckBox();
            groupBox4 = new GroupBox();
            linkClearLog = new LinkLabel();
            groupBox1.SuspendLayout();
            groupBox2.SuspendLayout();
            groupBox3.SuspendLayout();
            groupBox4.SuspendLayout();
            SuspendLayout();
            // 
            // textBoxServer
            // 
            textBoxServer.Location = new Point(124, 43);
            textBoxServer.Name = "textBoxServer";
            textBoxServer.Size = new Size(726, 30);
            textBoxServer.TabIndex = 1;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(15, 44);
            label2.Name = "label2";
            label2.Size = new Size(100, 24);
            label2.TabIndex = 3;
            label2.Text = "服务器地址";
            // 
            // buttonStart
            // 
            buttonStart.Location = new Point(138, 51);
            buttonStart.Name = "buttonStart";
            buttonStart.Size = new Size(114, 55);
            buttonStart.TabIndex = 4;
            buttonStart.Text = "启动";
            buttonStart.UseVisualStyleBackColor = true;
            buttonStart.Click += ButtonStart_Click;
            // 
            // process1
            // 
            process1.StartInfo.CreateNewProcessGroup = false;
            process1.StartInfo.Domain = "";
            process1.StartInfo.LoadUserProfile = false;
            process1.StartInfo.Password = null;
            process1.StartInfo.StandardErrorEncoding = null;
            process1.StartInfo.StandardInputEncoding = null;
            process1.StartInfo.StandardOutputEncoding = null;
            process1.StartInfo.UseCredentialsForNetworkingOnly = false;
            process1.StartInfo.UserName = "";
            process1.SynchronizingObject = this;
            // 
            // textBoxLog
            // 
            textBoxLog.BorderStyle = BorderStyle.None;
            textBoxLog.Cursor = Cursors.IBeam;
            textBoxLog.Location = new Point(12, 54);
            textBoxLog.Multiline = true;
            textBoxLog.Name = "textBoxLog";
            textBoxLog.ReadOnly = true;
            textBoxLog.ScrollBars = ScrollBars.Vertical;
            textBoxLog.Size = new Size(869, 315);
            textBoxLog.TabIndex = 5;
            // 
            // notifyIcon1
            // 
            notifyIcon1.Text = "notifyIcon1";
            notifyIcon1.Visible = true;
            // 
            // comboBoxServics
            // 
            comboBoxServics.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBoxServics.FormattingEnabled = true;
            comboBoxServics.Location = new Point(124, 43);
            comboBoxServics.Name = "comboBoxServics";
            comboBoxServics.Size = new Size(294, 32);
            comboBoxServics.TabIndex = 8;
            comboBoxServics.SelectedIndexChanged += ComboBox1_SelectedIndexChanged;
            // 
            // buttonSave
            // 
            buttonSave.Location = new Point(538, 34);
            buttonSave.Name = "buttonSave";
            buttonSave.Size = new Size(107, 48);
            buttonSave.TabIndex = 9;
            buttonSave.Text = "保存";
            buttonSave.UseVisualStyleBackColor = true;
            buttonSave.Click += ButtonSave_Click;
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new Point(23, 228);
            label4.Name = "label4";
            label4.Size = new Size(82, 24);
            label4.TabIndex = 10;
            label4.Text = "监听地址";
            // 
            // textBoxListen
            // 
            textBoxListen.Location = new Point(124, 227);
            textBoxListen.Name = "textBoxListen";
            textBoxListen.Size = new Size(726, 30);
            textBoxListen.TabIndex = 11;
            // 
            // label7
            // 
            label7.AutoSize = true;
            label7.Location = new Point(22, 91);
            label7.Name = "label7";
            label7.Size = new Size(82, 24);
            label7.TabIndex = 14;
            label7.Text = "认证令牌";
            // 
            // textBoxIp
            // 
            textBoxIp.Location = new Point(124, 135);
            textBoxIp.Name = "textBoxIp";
            textBoxIp.Size = new Size(726, 30);
            textBoxIp.TabIndex = 13;
            // 
            // textBoxToken
            // 
            textBoxToken.Location = new Point(124, 89);
            textBoxToken.Name = "textBoxToken";
            textBoxToken.Size = new Size(726, 30);
            textBoxToken.TabIndex = 12;
            // 
            // label8
            // 
            label8.AutoSize = true;
            label8.Location = new Point(18, 46);
            label8.Name = "label8";
            label8.Size = new Size(100, 24);
            label8.TabIndex = 24;
            label8.Text = "选择服务器";
            // 
            // buttonAdd
            // 
            buttonAdd.Location = new Point(427, 34);
            buttonAdd.Name = "buttonAdd";
            buttonAdd.Size = new Size(107, 48);
            buttonAdd.TabIndex = 25;
            buttonAdd.Text = "新增";
            buttonAdd.UseVisualStyleBackColor = true;
            buttonAdd.Click += ButtonAdd_Click;
            // 
            // buttonRename
            // 
            buttonRename.Location = new Point(649, 34);
            buttonRename.Name = "buttonRename";
            buttonRename.Size = new Size(107, 48);
            buttonRename.TabIndex = 26;
            buttonRename.Text = "重命名";
            buttonRename.UseVisualStyleBackColor = true;
            buttonRename.Click += ButtonRename_Click;
            // 
            // buttonDel
            // 
            buttonDel.Location = new Point(760, 34);
            buttonDel.Name = "buttonDel";
            buttonDel.Size = new Size(107, 48);
            buttonDel.TabIndex = 27;
            buttonDel.Text = "删除";
            buttonDel.UseVisualStyleBackColor = true;
            buttonDel.Click += ButtonDelete_Click;
            // 
            // groupBox1
            // 
            groupBox1.Controls.Add(buttonDel);
            groupBox1.Controls.Add(buttonAdd);
            groupBox1.Controls.Add(buttonRename);
            groupBox1.Controls.Add(comboBoxServics);
            groupBox1.Controls.Add(buttonSave);
            groupBox1.Controls.Add(label8);
            groupBox1.Location = new Point(21, 10);
            groupBox1.Name = "groupBox1";
            groupBox1.Size = new Size(881, 102);
            groupBox1.TabIndex = 28;
            groupBox1.TabStop = false;
            groupBox1.Text = "服务器设置";
            // 
            // groupBox2
            // 
            groupBox2.Controls.Add(linkLabelCidrsForm);
            groupBox2.Controls.Add(linkLabelCsvForm);
            groupBox2.Controls.Add(textBoxFallback);
            groupBox2.Controls.Add(textBoxServer);
            groupBox2.Controls.Add(label2);
            groupBox2.Controls.Add(label4);
            groupBox2.Controls.Add(textBoxListen);
            groupBox2.Controls.Add(textBoxToken);
            groupBox2.Controls.Add(label7);
            groupBox2.Controls.Add(textBoxIp);
            groupBox2.Location = new Point(21, 128);
            groupBox2.Name = "groupBox2";
            groupBox2.Size = new Size(881, 276);
            groupBox2.TabIndex = 29;
            groupBox2.TabStop = false;
            groupBox2.Text = "参数设置";
            // 
            // linkLabelCidrsForm
            // 
            linkLabelCidrsForm.ActiveLinkColor = Color.Maroon;
            linkLabelCidrsForm.AutoSize = true;
            linkLabelCidrsForm.LinkBehavior = LinkBehavior.NeverUnderline;
            linkLabelCidrsForm.LinkColor = Color.Maroon;
            linkLabelCidrsForm.Location = new Point(23, 139);
            linkLabelCidrsForm.Name = "linkLabelCidrsForm";
            linkLabelCidrsForm.Size = new Size(82, 24);
            linkLabelCidrsForm.TabIndex = 33;
            linkLabelCidrsForm.TabStop = true;
            linkLabelCidrsForm.Text = "优选地址";
            linkLabelCidrsForm.LinkClicked += linkLabelCidrs_LinkClicked;
            // 
            // linkLabelCsvForm
            // 
            linkLabelCsvForm.ActiveLinkColor = Color.Maroon;
            linkLabelCsvForm.AutoSize = true;
            linkLabelCsvForm.LinkBehavior = LinkBehavior.NeverUnderline;
            linkLabelCsvForm.LinkColor = Color.Maroon;
            linkLabelCsvForm.Location = new Point(26, 183);
            linkLabelCsvForm.Name = "linkLabelCsvForm";
            linkLabelCsvForm.Size = new Size(74, 24);
            linkLabelCsvForm.TabIndex = 32;
            linkLabelCsvForm.TabStop = true;
            linkLabelCsvForm.Text = "ProxyIP";
            linkLabelCsvForm.LinkClicked += linkLabelCsv_LinkClicked;
            // 
            // textBoxFallback
            // 
            textBoxFallback.Location = new Point(124, 181);
            textBoxFallback.Name = "textBoxFallback";
            textBoxFallback.Size = new Size(726, 30);
            textBoxFallback.TabIndex = 27;
            // 
            // groupBox3
            // 
            groupBox3.Controls.Add(checkBoxAutoStartHide);
            groupBox3.Controls.Add(buttonStop);
            groupBox3.Controls.Add(checkBoxAutoStart);
            groupBox3.Controls.Add(checkBoxSystemProxy);
            groupBox3.Controls.Add(buttonStart);
            groupBox3.Location = new Point(21, 420);
            groupBox3.Name = "groupBox3";
            groupBox3.Size = new Size(881, 147);
            groupBox3.TabIndex = 30;
            groupBox3.TabStop = false;
            groupBox3.Text = "控制";
            // 
            // checkBoxAutoStartHide
            // 
            checkBoxAutoStartHide.AutoSize = true;
            checkBoxAutoStartHide.Location = new Point(22, 103);
            checkBoxAutoStartHide.Name = "checkBoxAutoStartHide";
            checkBoxAutoStartHide.Size = new Size(108, 28);
            checkBoxAutoStartHide.TabIndex = 11;
            checkBoxAutoStartHide.Text = "隐藏窗体";
            checkBoxAutoStartHide.UseVisualStyleBackColor = true;
            checkBoxAutoStartHide.CheckedChanged += CheckBoxAutoStartHide_CheckedChanged;
            // 
            // buttonStop
            // 
            buttonStop.Enabled = false;
            buttonStop.Location = new Point(269, 51);
            buttonStop.Name = "buttonStop";
            buttonStop.Size = new Size(114, 55);
            buttonStop.TabIndex = 9;
            buttonStop.Text = "停止";
            buttonStop.UseVisualStyleBackColor = true;
            buttonStop.Click += ButtonStop_Click;
            // 
            // checkBoxAutoStart
            // 
            checkBoxAutoStart.AutoSize = true;
            checkBoxAutoStart.Location = new Point(22, 67);
            checkBoxAutoStart.Name = "checkBoxAutoStart";
            checkBoxAutoStart.Size = new Size(108, 28);
            checkBoxAutoStart.TabIndex = 8;
            checkBoxAutoStart.Text = "开机启动";
            checkBoxAutoStart.UseVisualStyleBackColor = true;
            checkBoxAutoStart.CheckedChanged += CheckBoxAutoStart_CheckedChanged;
            // 
            // checkBoxSystemProxy
            // 
            checkBoxSystemProxy.AutoSize = true;
            checkBoxSystemProxy.Location = new Point(22, 31);
            checkBoxSystemProxy.Name = "checkBoxSystemProxy";
            checkBoxSystemProxy.Size = new Size(108, 28);
            checkBoxSystemProxy.TabIndex = 7;
            checkBoxSystemProxy.Text = "系统代理";
            checkBoxSystemProxy.UseVisualStyleBackColor = true;
            checkBoxSystemProxy.CheckedChanged += CheckBoxSystemProxy_CheckedChanged;
            // 
            // groupBox4
            // 
            groupBox4.Controls.Add(linkClearLog);
            groupBox4.Controls.Add(textBoxLog);
            groupBox4.Location = new Point(21, 583);
            groupBox4.Name = "groupBox4";
            groupBox4.Size = new Size(881, 382);
            groupBox4.TabIndex = 31;
            groupBox4.TabStop = false;
            groupBox4.Text = "运行日志";
            // 
            // linkClearLog
            // 
            linkClearLog.AutoSize = true;
            linkClearLog.LinkBehavior = LinkBehavior.NeverUnderline;
            linkClearLog.LinkColor = Color.Gray;
            linkClearLog.Location = new Point(762, 19);
            linkClearLog.Name = "linkClearLog";
            linkClearLog.Size = new Size(82, 24);
            linkClearLog.TabIndex = 7;
            linkClearLog.TabStop = true;
            linkClearLog.Text = "清空日志";
            linkClearLog.VisitedLinkColor = Color.Gray;
            linkClearLog.LinkClicked += LinkClearLog_LinkClicked;
            // 
            // App
            // 
            AutoScaleDimensions = new SizeF(11F, 24F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(920, 979);
            Controls.Add(groupBox1);
            Controls.Add(groupBox2);
            Controls.Add(groupBox3);
            Controls.Add(groupBox4);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Name = "App";
            RightToLeft = RightToLeft.No;
            RightToLeftLayout = true;
            Text = "ZKS Workers Client";
            Load += Form1_Load;
            groupBox1.ResumeLayout(false);
            groupBox1.PerformLayout();
            groupBox2.ResumeLayout(false);
            groupBox2.PerformLayout();
            groupBox3.ResumeLayout(false);
            groupBox3.PerformLayout();
            groupBox4.ResumeLayout(false);
            groupBox4.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private TextBox textBoxServer;
        private Label label2;
        private Button buttonStart;
        private System.Diagnostics.Process process1;
        private TextBox textBoxLog;
        private NotifyIcon notifyIcon1;
        private ComboBox comboBoxServics;
        private Button buttonSave;
        private Label label4;
        private TextBox textBoxListen;
        private Label label7;
        private TextBox textBoxIp;
        private TextBox textBoxToken;
        private Label label8;
        private Button buttonDel;
        private Button buttonRename;
        private Button buttonAdd;
        private GroupBox groupBox1;
        private GroupBox groupBox2;
        private GroupBox groupBox3;
        private GroupBox groupBox4;
        private Button buttonStop;
        private CheckBox checkBoxAutoStart;
        private CheckBox checkBoxSystemProxy;
        private CheckBox checkBoxAutoStartHide;
        private LinkLabel linkClearLog;
        private TextBox textBoxFallback;
        private LinkLabel linkLabelCsvForm;
        private LinkLabel linkLabelCidrsForm;
    }
}
