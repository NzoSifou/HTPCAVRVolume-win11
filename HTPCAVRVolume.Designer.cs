namespace HTPCAVRVolume
{
    partial class HTPCAVRVolume
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
            this.components = new System.ComponentModel.Container();
            this.lblDevice = new System.Windows.Forms.Label();
            this.cmbDevice = new System.Windows.Forms.ComboBox();
            this.btnSave = new System.Windows.Forms.Button();
            this.lblIP = new System.Windows.Forms.Label();
            this.tbIP = new System.Windows.Forms.TextBox();
            this.lblStep = new System.Windows.Forms.Label();
            this.numStep = new System.Windows.Forms.NumericUpDown();
            this.lblMaxVolume = new System.Windows.Forms.Label();
            this.numMaxVolume = new System.Windows.Forms.NumericUpDown();
            this.lblUnit = new System.Windows.Forms.Label();
            this.cmbUnit = new System.Windows.Forms.ComboBox();
            this.chkShowOsd = new System.Windows.Forms.CheckBox();
            this.chkOsdRemote = new System.Windows.Forms.CheckBox();
            this.lblVolume = new System.Windows.Forms.Label();
            this.btnDown = new System.Windows.Forms.Button();
            this.trkVolume = new System.Windows.Forms.TrackBar();
            this.btnUp = new System.Windows.Forms.Button();
            this.btnToggleMute = new System.Windows.Forms.Button();
            this.lblStatus = new System.Windows.Forms.Label();
            this.notifyIcon = new System.Windows.Forms.NotifyIcon(this.components);
            this.toolTip = new System.Windows.Forms.ToolTip(this.components);
            ((System.ComponentModel.ISupportInitialize)(this.numStep)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numMaxVolume)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.trkVolume)).BeginInit();
            this.SuspendLayout();
            //
            // lblDevice
            //
            this.lblDevice.AutoSize = true;
            this.lblDevice.Location = new System.Drawing.Point(12, 15);
            this.lblDevice.Name = "lblDevice";
            this.lblDevice.Size = new System.Drawing.Size(44, 13);
            this.lblDevice.TabIndex = 0;
            this.lblDevice.Text = "Device:";
            //
            // cmbDevice
            //
            this.cmbDevice.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbDevice.FormattingEnabled = true;
            this.cmbDevice.Items.AddRange(new object[] {
            "DenonMarantz",
            "StormAudio"});
            this.cmbDevice.Location = new System.Drawing.Point(78, 12);
            this.cmbDevice.Name = "cmbDevice";
            this.cmbDevice.Size = new System.Drawing.Size(160, 21);
            this.cmbDevice.TabIndex = 1;
            //
            // btnSave
            //
            this.btnSave.Location = new System.Drawing.Point(252, 12);
            this.btnSave.Name = "btnSave";
            this.btnSave.Size = new System.Drawing.Size(116, 47);
            this.btnSave.TabIndex = 2;
            this.btnSave.Text = "Save";
            this.btnSave.UseVisualStyleBackColor = true;
            this.btnSave.Click += new System.EventHandler(this.BtnSave_Click);
            //
            // lblIP
            //
            this.lblIP.AutoSize = true;
            this.lblIP.Location = new System.Drawing.Point(12, 42);
            this.lblIP.Name = "lblIP";
            this.lblIP.Size = new System.Drawing.Size(20, 13);
            this.lblIP.TabIndex = 3;
            this.lblIP.Text = "IP:";
            //
            // tbIP
            //
            this.tbIP.Location = new System.Drawing.Point(78, 39);
            this.tbIP.Name = "tbIP";
            this.tbIP.Size = new System.Drawing.Size(160, 20);
            this.tbIP.TabIndex = 4;
            //
            // lblStep
            //
            this.lblStep.AutoSize = true;
            this.lblStep.Location = new System.Drawing.Point(12, 69);
            this.lblStep.Name = "lblStep";
            this.lblStep.Size = new System.Drawing.Size(55, 13);
            this.lblStep.TabIndex = 5;
            this.lblStep.Text = "Step (dB):";
            //
            // numStep
            //
            this.numStep.DecimalPlaces = 1;
            this.numStep.Increment = new decimal(new int[] { 5, 0, 0, 65536 });
            this.numStep.Location = new System.Drawing.Point(78, 66);
            this.numStep.Maximum = new decimal(new int[] { 10, 0, 0, 0 });
            this.numStep.Minimum = new decimal(new int[] { 5, 0, 0, 65536 });
            this.numStep.Name = "numStep";
            this.numStep.Size = new System.Drawing.Size(52, 20);
            this.numStep.TabIndex = 6;
            this.numStep.Value = new decimal(new int[] { 5, 0, 0, 65536 });
            this.numStep.ValueChanged += new System.EventHandler(this.NumStep_ValueChanged);
            this.toolTip.SetToolTip(this.numStep, "How much one press, one detent of a volume wheel, or one notch of the slider moves" +
                    " the volume.");
            //
            // lblMaxVolume
            //
            this.lblMaxVolume.AutoSize = true;
            this.lblMaxVolume.Location = new System.Drawing.Point(146, 69);
            this.lblMaxVolume.Name = "lblMaxVolume";
            this.lblMaxVolume.Size = new System.Drawing.Size(97, 13);
            this.lblMaxVolume.TabIndex = 7;
            this.lblMaxVolume.Text = "Max volume (dB):";
            //
            // numMaxVolume
            //
            this.numMaxVolume.DecimalPlaces = 1;
            this.numMaxVolume.Increment = new decimal(new int[] { 5, 0, 0, 65536 });
            this.numMaxVolume.Location = new System.Drawing.Point(252, 66);
            this.numMaxVolume.Maximum = new decimal(new int[] { 18, 0, 0, 0 });
            this.numMaxVolume.Minimum = new decimal(new int[] { 79, 0, 0, -2147483648 });
            this.numMaxVolume.Name = "numMaxVolume";
            this.numMaxVolume.Size = new System.Drawing.Size(60, 20);
            this.numMaxVolume.TabIndex = 8;
            this.numMaxVolume.Value = new decimal(new int[] { 18, 0, 0, 0 });
            this.numMaxVolume.ValueChanged += new System.EventHandler(this.NumMaxVolume_ValueChanged);
            this.toolTip.SetToolTip(this.numMaxVolume, "The loudest the app will go, and the top of the slider and of the on-screen displa" +
                    "y. Filled in from the AVR when it reports its own maximum.");
            //
            // lblUnit
            //
            this.lblUnit.AutoSize = true;
            this.lblUnit.Location = new System.Drawing.Point(12, 96);
            this.lblUnit.Name = "lblUnit";
            this.lblUnit.Size = new System.Drawing.Size(66, 13);
            this.lblUnit.TabIndex = 9;
            this.lblUnit.Text = "Display unit:";
            //
            // cmbUnit
            //
            this.cmbUnit.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbUnit.FormattingEnabled = true;
            this.cmbUnit.Items.AddRange(new object[] {
            "dB",
            "Scale"});
            this.cmbUnit.Location = new System.Drawing.Point(88, 93);
            this.cmbUnit.Name = "cmbUnit";
            this.cmbUnit.Size = new System.Drawing.Size(72, 21);
            this.cmbUnit.TabIndex = 10;
            this.cmbUnit.SelectedIndexChanged += new System.EventHandler(this.CmbUnit_SelectedIndexChanged);
            this.toolTip.SetToolTip(this.cmbUnit, "dB is what the AVR shows on its own display; Scale is the 0 to 98 the protocol use" +
                    "s.");
            //
            // chkShowOsd
            //
            this.chkShowOsd.AutoSize = true;
            this.chkShowOsd.Checked = true;
            this.chkShowOsd.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkShowOsd.Location = new System.Drawing.Point(176, 95);
            this.chkShowOsd.Name = "chkShowOsd";
            this.chkShowOsd.Size = new System.Drawing.Size(125, 17);
            this.chkShowOsd.TabIndex = 11;
            this.chkShowOsd.Text = "Show volume display";
            this.chkShowOsd.UseVisualStyleBackColor = true;
            //
            // chkOsdRemote
            //
            this.chkOsdRemote.AutoSize = true;
            this.chkOsdRemote.Checked = true;
            this.chkOsdRemote.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkOsdRemote.Location = new System.Drawing.Point(12, 120);
            this.chkOsdRemote.Name = "chkOsdRemote";
            this.chkOsdRemote.Size = new System.Drawing.Size(225, 17);
            this.chkOsdRemote.TabIndex = 12;
            this.chkOsdRemote.Text = "Also when the AVR\'s own remote is used";
            this.chkOsdRemote.UseVisualStyleBackColor = true;
            //
            // lblVolume
            //
            this.lblVolume.Font = new System.Drawing.Font("Segoe UI", 12F);
            this.lblVolume.Location = new System.Drawing.Point(12, 148);
            this.lblVolume.Name = "lblVolume";
            this.lblVolume.Size = new System.Drawing.Size(356, 24);
            this.lblVolume.TabIndex = 13;
            this.lblVolume.Text = "--";
            this.lblVolume.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            //
            // btnDown
            //
            this.btnDown.Location = new System.Drawing.Point(12, 178);
            this.btnDown.Name = "btnDown";
            this.btnDown.Size = new System.Drawing.Size(32, 26);
            this.btnDown.TabIndex = 14;
            this.btnDown.Text = "-";
            this.btnDown.UseVisualStyleBackColor = true;
            this.btnDown.Click += new System.EventHandler(this.BtnVolDown_Click);
            this.toolTip.SetToolTip(this.btnDown, "Turn the volume down one step");
            //
            // trkVolume
            //
            this.trkVolume.AutoSize = false;
            this.trkVolume.Location = new System.Drawing.Point(48, 176);
            this.trkVolume.Maximum = 196;
            this.trkVolume.Name = "trkVolume";
            this.trkVolume.Size = new System.Drawing.Size(180, 30);
            this.trkVolume.TabIndex = 15;
            this.trkVolume.TickStyle = System.Windows.Forms.TickStyle.None;
            this.trkVolume.ValueChanged += new System.EventHandler(this.TrkVolume_ValueChanged);
            this.trkVolume.MouseDown += new System.Windows.Forms.MouseEventHandler(this.TrkVolume_MouseDown);
            this.trkVolume.MouseMove += new System.Windows.Forms.MouseEventHandler(this.TrkVolume_MouseMove);
            this.trkVolume.MouseUp += new System.Windows.Forms.MouseEventHandler(this.TrkVolume_MouseUp);
            //
            // btnUp
            //
            this.btnUp.Location = new System.Drawing.Point(232, 178);
            this.btnUp.Name = "btnUp";
            this.btnUp.Size = new System.Drawing.Size(32, 26);
            this.btnUp.TabIndex = 16;
            this.btnUp.Text = "+";
            this.btnUp.UseVisualStyleBackColor = true;
            this.btnUp.Click += new System.EventHandler(this.BtnVolUp_Click);
            this.toolTip.SetToolTip(this.btnUp, "Turn the volume up one step");
            //
            // btnToggleMute
            //
            this.btnToggleMute.Location = new System.Drawing.Point(272, 178);
            this.btnToggleMute.Name = "btnToggleMute";
            this.btnToggleMute.Size = new System.Drawing.Size(96, 26);
            this.btnToggleMute.TabIndex = 17;
            this.btnToggleMute.Text = "Mute / Unmute";
            this.btnToggleMute.UseVisualStyleBackColor = true;
            this.btnToggleMute.Click += new System.EventHandler(this.BtnToggleMute_Click);
            //
            // lblStatus
            //
            this.lblStatus.AutoEllipsis = true;
            this.lblStatus.Location = new System.Drawing.Point(12, 214);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(356, 15);
            this.lblStatus.TabIndex = 18;
            this.lblStatus.Text = "Starting...";
            //
            // notifyIcon
            //
            this.notifyIcon.Text = "HTPCAVRVolume";
            this.notifyIcon.Visible = true;
            this.notifyIcon.MouseDoubleClick += new System.Windows.Forms.MouseEventHandler(this.NotifyIcon_MouseDoubleClick);
            //
            // HTPCAVRVolume
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(380, 240);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.btnToggleMute);
            this.Controls.Add(this.btnUp);
            this.Controls.Add(this.trkVolume);
            this.Controls.Add(this.btnDown);
            this.Controls.Add(this.lblVolume);
            this.Controls.Add(this.chkOsdRemote);
            this.Controls.Add(this.chkShowOsd);
            this.Controls.Add(this.cmbUnit);
            this.Controls.Add(this.lblUnit);
            this.Controls.Add(this.numMaxVolume);
            this.Controls.Add(this.lblMaxVolume);
            this.Controls.Add(this.numStep);
            this.Controls.Add(this.lblStep);
            this.Controls.Add(this.btnSave);
            this.Controls.Add(this.tbIP);
            this.Controls.Add(this.lblIP);
            this.Controls.Add(this.lblDevice);
            this.Controls.Add(this.cmbDevice);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Name = "HTPCAVRVolume";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "HTPCAVRVolume";
            this.FormClosed += new System.Windows.Forms.FormClosedEventHandler(this.HTPCAVRVolume_FormClosed);
            this.Load += new System.EventHandler(this.HTPCAVRVolume_Load);
            this.Shown += new System.EventHandler(this.HTPCAVRVolume_Shown);
            this.Resize += new System.EventHandler(this.HTPCAVRVolume_Resize);
            ((System.ComponentModel.ISupportInitialize)(this.numStep)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numMaxVolume)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.trkVolume)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label lblDevice;
        private System.Windows.Forms.ComboBox cmbDevice;
        private System.Windows.Forms.Button btnSave;
        private System.Windows.Forms.Label lblIP;
        private System.Windows.Forms.TextBox tbIP;
        private System.Windows.Forms.Label lblStep;
        private System.Windows.Forms.NumericUpDown numStep;
        private System.Windows.Forms.Label lblMaxVolume;
        private System.Windows.Forms.NumericUpDown numMaxVolume;
        private System.Windows.Forms.Label lblUnit;
        private System.Windows.Forms.ComboBox cmbUnit;
        private System.Windows.Forms.CheckBox chkShowOsd;
        private System.Windows.Forms.CheckBox chkOsdRemote;
        private System.Windows.Forms.Label lblVolume;
        private System.Windows.Forms.Button btnDown;
        private System.Windows.Forms.TrackBar trkVolume;
        private System.Windows.Forms.Button btnUp;
        private System.Windows.Forms.Button btnToggleMute;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.NotifyIcon notifyIcon;
        private System.Windows.Forms.ToolTip toolTip;
    }
}
