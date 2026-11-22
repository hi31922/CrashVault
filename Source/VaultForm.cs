using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CrashVault
{
    internal sealed class VaultForm : Form
    {
        private static readonly Color Background = Color.FromArgb(242, 244, 247);
        private static readonly Color Surface = Color.White;
        private static readonly Color Ink = Color.FromArgb(32, 41, 53);
        private static readonly Color Muted = Color.FromArgb(84, 99, 116);
        private static readonly Color Divider = Color.FromArgb(221, 227, 235);
        private static readonly Color Green = Color.FromArgb(38, 117, 78);
        private static readonly Color Yellow = Color.FromArgb(151, 98, 13);
        private static readonly Color Red = Color.FromArgb(168, 55, 65);

        private readonly VaultStore store;
        private readonly Icon windowIcon;
        private readonly string startupIssue;
        private readonly TextBox searchBox;
        private readonly ComboBox priorityFilter;
        private readonly EventViewport eventList;
        private readonly Label eventCountLabel;
        private readonly Label statusLabel;
        private readonly Label emptyLabel;
        private readonly RichTextBox detailBox;
        private readonly Button copyButton;
        private readonly Timer pollTimer = new Timer { Interval = 3000 };
        private readonly Timer searchTimer = new Timer { Interval = 300 };
        private readonly Font interfaceFont = new Font("Segoe UI", 10);
        private readonly Font boldFont = new Font("Segoe UI", 10, FontStyle.Bold);
        private readonly Font captionFont = new Font("Segoe UI", 9);
        private readonly Font titleFont = new Font("Segoe UI", 17, FontStyle.Bold);

        private List<VaultEvent> entries = new List<VaultEvent>();
        private long revision = -1;
        private long displayedEventId = -1;
        private bool updatingList;
        private bool initialLayout;
        private string readIssue;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr flags, string text);

        public VaultForm(VaultStore store, string startupIssue)
        {
            this.store = store;
            this.startupIssue = startupIssue;
            Text = "Event history";
            using (var iconData = typeof(VaultForm).Assembly.GetManifestResourceStream("CrashVault.WindowIcon"))
            {
                if (iconData != null)
                {
                    windowIcon = new Icon(iconData);
                    Icon = windowIcon;
                }
            }
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1156, 700);
            MinimumSize = new Size(835, 490);
            BackColor = Background;
            ForeColor = Ink;
            Font = interfaceFont;

            var frame = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 3,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = new Padding(16, 12, 16, 8)
            };
            frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            Controls.Add(frame);

            var toolbar = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Fill, Margin = Padding.Empty };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            frame.Controls.Add(toolbar, 0, 0);

            searchBox = new TextBox
            {
                AccessibleName = "Search events",
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, 2, 10, 8),
                MaxLength = 250
            };
            searchBox.HandleCreated += delegate
            {
                SendMessage(searchBox.Handle, 0x1501, IntPtr.Zero, "Search events, sources or IDs");
            };
            searchBox.TextChanged += delegate
            {
                searchTimer.Stop();
                searchTimer.Start();
            };
            toolbar.Controls.Add(searchBox, 0, 0);

            priorityFilter = new ComboBox
            {
                AccessibleName = "Filter by priority",
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, 2, 10, 8)
            };
            priorityFilter.Items.AddRange(new object[] { "All priorities", "Low priority", "Moderate priority", "High priority" });
            priorityFilter.SelectedIndex = 0;
            priorityFilter.SelectedIndexChanged += delegate { FilterEvents(); };
            toolbar.Controls.Add(priorityFilter, 1, 0);

            var refreshButton = MakeButton("Refresh");
            refreshButton.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            refreshButton.Margin = new Padding(0, 2, 0, 8);
            refreshButton.Click += delegate { RefreshView(true); };
            toolbar.Controls.Add(refreshButton, 2, 0);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.None,
                Margin = Padding.Empty,
                SplitterWidth = 10
            };
            frame.Controls.Add(split, 0, 1);

            var eventPanel = MakeSurface();
            eventPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            eventPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            split.Panel1.Controls.Add(eventPanel);

            eventList = new EventViewport { Dock = DockStyle.Fill, Font = interfaceFont };
            eventList.SelectedIndexChanged += delegate
            {
                if (!updatingList)
                    ShowSelectedEvent();
            };

            var listHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Surface };
            listHost.Controls.Add(eventList);
            emptyLabel = MakeLabel(String.Empty, Muted);
            emptyLabel.Dock = DockStyle.Fill;
            emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            emptyLabel.Padding = new Padding(12);
            emptyLabel.Visible = false;
            listHost.Controls.Add(emptyLabel);
            eventPanel.Controls.Add(listHost, 0, 0);

            eventCountLabel = MakeLabel(String.Empty, Muted);
            eventCountLabel.Margin = new Padding(10, 0, 8, 0);
            eventPanel.Controls.Add(eventCountLabel, 0, 1);

            var details = MakeSurface();
            details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            split.Panel2.Controls.Add(details);

            detailBox = new RichTextBox
            {
                AccessibleName = "Selected event details",
                Dock = DockStyle.Fill,
                BackColor = Surface,
                ForeColor = Ink,
                BorderStyle = BorderStyle.None,
                DetectUrls = false,
                Font = interfaceFont,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Margin = new Padding(17, 15, 12, 5)
            };
            details.Controls.Add(detailBox, 0, 0);

            var detailFooter = new TableLayoutPanel
            {
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = new Padding(8, 5, 7, 4)
            };
            detailFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            detailFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 114));
            details.Controls.Add(detailFooter, 0, 1);
            copyButton = MakeButton("Copy details");
            copyButton.Click += CopyDetails;
            detailFooter.Controls.Add(copyButton, 1, 0);

            statusLabel = MakeLabel(String.Empty, Muted);
            statusLabel.Margin = new Padding(2, 1, 0, 0);
            frame.Controls.Add(statusLabel, 0, 2);

            searchTimer.Tick += delegate
            {
                searchTimer.Stop();
                FilterEvents();
            };
            pollTimer.Tick += delegate { RefreshView(false); };
            Shown += delegate
            {
                SetInitialSplitterPosition(split);
                RefreshView(true);
                pollTimer.Start();
            };
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F))
            {
                searchBox.Focus();
                searchBox.SelectAll();
                return true;
            }
            if (keyData == Keys.F5)
            {
                RefreshView(true);
                return true;
            }
            if (keyData == Keys.Escape && searchBox.Focused && searchBox.TextLength > 0)
            {
                searchBox.Clear();
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        private void SetInitialSplitterPosition(SplitContainer split)
        {
            if (initialLayout)
                return;
            initialLayout = true;
            float scale = split.Width / (float)Math.Max(ClientSize.Width - 32, 1);
            split.Panel1MinSize = (int)(398 * scale);
            split.Panel2MinSize = (int)(293 * scale);
            split.SplitterDistance = Math.Max(split.Panel1MinSize,
                Math.Min((int)(split.Width * 0.57f), split.Width - split.Panel2MinSize - split.SplitterWidth));
        }

        private static TableLayoutPanel MakeSurface()
        {
            return new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 2,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private Label MakeLabel(string text, Color color)
        {
            return new Label
            {
                Text = text,
                ForeColor = color,
                Font = captionFont,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
        }

        private static Button MakeButton(string text)
        {
            return new Button
            {
                Text = text,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Height = 30,
                Margin = new Padding(3, 1, 0, 1),
                FlatStyle = FlatStyle.Flat,
                BackColor = Surface,
                ForeColor = Ink,
                FlatAppearance = { BorderColor = Divider, MouseOverBackColor = Background }
            };
        }

        private void FilterEvents()
        {
            searchTimer.Stop();
            RefreshView(true, false);
        }

        private VaultEvent EventAt(int index)
        {
            return index >= 0 && index < entries.Count ? entries[index] : null;
        }

        private void RefreshView(bool force, bool preservePosition = true)
        {
            string collectorStatus = null;
            try
            {
                long currentRevision = store.GetRevision();
                if (force || revision != currentRevision || readIssue != null)
                {
                    ListEvents(store.ReadEventSummaries(searchBox.Text, priorityFilter.SelectedIndex), preservePosition);
                    revision = currentRevision;
                }
                collectorStatus = store.GetCollectorStatus();
                readIssue = null;
            }
            catch (Exception error)
            {
                readIssue = error.Message;
                if (entries.Count == 0)
                {
                    emptyLabel.Text = "Could not read the event history.";
                    emptyLabel.Visible = true;
                }
            }
            UpdateStatus(collectorStatus);
        }

        private void ListEvents(List<VaultEvent> events, bool preservePosition)
        {
            VaultEvent selected = preservePosition ? EventAt(eventList.SelectedIndex) : null;
            int currentTop = eventList.FirstVisibleIndex;
            VaultEvent scrollAnchor = preservePosition && currentTop > 0 ? EventAt(currentTop) : null;
            int selectedIndex = selected == null ? 0 :
                Math.Max(0, events.FindIndex(entry => entry.RowId == selected.RowId));
            int firstIndex = scrollAnchor == null ? 0 :
                Math.Max(0, events.FindIndex(entry => entry.RowId == scrollAnchor.RowId));

            updatingList = true;
            try
            {
                entries = events;
                eventList.ShowEvents(entries, entries.Count == 0 ? -1 : selectedIndex, firstIndex);
                eventList.Visible = entries.Count > 0;
            }
            finally
            {
                updatingList = false;
            }

            bool filtered = searchBox.Text.Trim().Length > 0 || priorityFilter.SelectedIndex > 0;
            emptyLabel.Text = filtered ? "No events match these filters." : "No events recorded yet.";
            emptyLabel.Visible = entries.Count == 0;
            eventCountLabel.Text = entries.Count == 1 ? "1 event" : String.Format("{0:N0} events", entries.Count);
            ShowSelectedEvent();
        }


        private void ShowSelectedEvent()
        {
            VaultEvent summary = EventAt(eventList.SelectedIndex);
            if (summary == null)
            {
                displayedEventId = -1;
                detailBox.Clear();
                copyButton.Enabled = false;
                return;
            }
            if (displayedEventId == summary.RowId)
                return;

            VaultEvent entry;
            try
            {
                entry = store.ReadEvent(summary.RowId);
            }
            catch (IOException)
            {
                displayedEventId = -1;
                detailBox.Text = "Could not read this event. Refresh to try again.";
                copyButton.Enabled = false;
                return;
            }

            if (entry == null)
            {
                displayedEventId = -1;
                detailBox.Text = "This event is unavailable.";
                copyButton.Enabled = false;
                return;
            }


            displayedEventId = entry.RowId;
            copyButton.Enabled = true;
            detailBox.SuspendLayout();
            try
            {
                detailBox.Clear();
                AppendDetails(PriorityName(entry.Severity) + " priority", boldFont, PriorityColor(entry.Severity));
                AppendDetails("\n", interfaceFont, Ink);
                AppendDetails(entry.Title + "\n", titleFont, Ink);
                AppendDetails(FormatTime(entry.UtcTicks, "F") + "\n\n", captionFont, Muted);
                AddDetailSection("What happened", entry.Explanation);
                AddDetailSection("Suggested steps", entry.Steps);
                AppendDetails("Windows event\n", boldFont, Ink);
                AppendDetails("Source  " + entry.Provider + "\n" +
                    "Channel  " + entry.Channel + "\n" +
                    "Event ID  " + entry.EventId + "\n" +
                    "Windows level  " + WindowsLevelName(entry.WindowsLevel) + "\n\n", captionFont, Muted);
                AppendDetails(String.IsNullOrWhiteSpace(entry.Detail) ?
                    "Windows did not provide a description for this record." : entry.Detail.Trim(), interfaceFont, Ink);
                detailBox.Select(0, 0);
                detailBox.ScrollToCaret();
            }
            finally
            {
                detailBox.ResumeLayout();
            }
        }

        private void AddDetailSection(string title, string body)
        {
            if (String.IsNullOrWhiteSpace(body))
                return;

            AppendDetails(title + "\n", boldFont, Ink);
            AppendDetails(body.Trim() + "\n\n", interfaceFont, Ink);
        }

        private void AppendDetails(string text, Font font, Color color)
        {
            detailBox.Select(detailBox.TextLength, 0);
            detailBox.SelectionFont = font;
            detailBox.SelectionColor = color;
            detailBox.AppendText(text);
        }

        private void CopyDetails(object sender, EventArgs args)
        {
            try
            {
                if (detailBox.TextLength > 0)
                    Clipboard.SetText(detailBox.Text);
            }
            catch (ExternalException)
            {
                MessageBox.Show(this, "The clipboard is busy. Try copying the event again.", "Copy details",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void UpdateStatus(string collectorStatus)
        {
            bool running = Program.IsCollectorRunning();
            string summary = running ? "Background collector running" : "Background collector stopped. Reopen the executable to start it.";
            string context = running ? collectorStatus : null;
            Color color = running ? Green : Muted;
            if (!String.IsNullOrWhiteSpace(startupIssue))
            {
                summary += "  ·  " + startupIssue;
                color = Yellow;
            }
            if (!String.IsNullOrWhiteSpace(readIssue))
            {
                summary = "Could not read the event history. " + summary;
                context = readIssue;
                color = Red;
            }
            string text = String.IsNullOrWhiteSpace(context) ? summary : summary + "  ·  " + context;
            if (statusLabel.Text != text)
                statusLabel.Text = text;
            statusLabel.ForeColor = color;
        }

        internal static string FormatTime(long ticks, string format)
        {
            if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
                return "Time unavailable";
            return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString(format);
        }

        private static string WindowsLevelName(int level)
        {
            switch (level)
            {
                case 1: return "Critical";
                case 2: return "Error";
                case 3: return "Warning";
                case 4: return "Information";
                case 5: return "Verbose";
                default: return level > 0 ? level.ToString() : "Unspecified";
            }
        }

        internal static string PriorityName(int level)
        {
            switch (level)
            {
                case 3: return "High";
                case 2: return "Moderate";
                default: return "Low";
            }
        }

        internal static Color PriorityColor(int level)
        {
            switch (level)
            {
                case 3: return Red;
                case 2: return Yellow;
                default: return Green;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                pollTimer.Dispose();
                searchTimer.Dispose();
            }
            base.Dispose(disposing);
            if (disposing)
            {
                interfaceFont.Dispose();
                boldFont.Dispose();
                captionFont.Dispose();
                titleFont.Dispose();
                if (windowIcon != null)
                    windowIcon.Dispose();
            }
        }

    }
}
