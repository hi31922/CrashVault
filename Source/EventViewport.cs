using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CrashVault
{
    internal sealed class EventViewport : UserControl
    {
        private const int MaxRenderedRows = 60;
        private const int ItemsPerPageMessage = 0x1028;
        private const int WheelDelta = 120;

        private readonly ViewportList list;
        private readonly VScrollBar scrollBar;
        private readonly ColumnHeader eventColumn;
        private readonly int priorityWidth;
        private readonly int timeWidth;
        private readonly ListViewItem[] visibleRows = new ListViewItem[MaxRenderedRows];

        private List<VaultEvent> entries = new List<VaultEvent>();
        private int selectedIndex = -1;
        private int firstVisibleIndex;
        private int pageSize = 1;
        private int wheelRemainder;
        private bool rendering;
        private bool relayoutQueued;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

        public EventViewport()
        {
            TabStop = false;
            BackColor = Color.White;
            list = new ViewportList(this)
            {
                AccessibleName = "Recorded Windows events",
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(32, 41, 53),
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                HideSelection = false,
                MultiSelect = false,
                Scrollable = false,
                View = View.Details,
                VirtualMode = true
            };
            priorityWidth = TextRenderer.MeasureText("● Moderate", list.Font).Width + 20;
            timeWidth = TextRenderer.MeasureText(DateTime.Now.ToString("g"), list.Font).Width + 24;
            list.Columns.Add("Priority", priorityWidth);
            eventColumn = list.Columns.Add("Event", 140);
            list.Columns.Add("Time", timeWidth);
            list.RetrieveVirtualItem += RetrieveRow;
            list.SelectedIndexChanged += ReadRowSelection;

            scrollBar = new VScrollBar
            {
                AccessibleName = "Scroll recorded Windows events",
                Dock = DockStyle.Right,
                Width = SystemInformation.VerticalScrollBarWidth,
                TabStop = false,
                SmallChange = 1
            };
            scrollBar.ValueChanged += delegate
            {
                if (!rendering)
                    ScrollTo(scrollBar.Value);
            };

            Controls.Add(list);
            Controls.Add(scrollBar);
            list.ClientSizeChanged += QueueRelayout;
            list.HandleCreated += QueueRelayout;
        }

        public int SelectedIndex
        {
            get { return selectedIndex; }
        }

        public int FirstVisibleIndex
        {
            get { return firstVisibleIndex; }
        }

        public event EventHandler SelectedIndexChanged;

        public void ShowEvents(List<VaultEvent> entries, int selectedIndex, int firstIndex)
        {
            this.entries = entries;
            this.selectedIndex = selectedIndex >= 0 && selectedIndex < entries.Count ? selectedIndex : -1;
            firstVisibleIndex = firstIndex;
            RenderPage();
        }

        private void QueueRelayout(object sender, EventArgs args)
        {
            if (rendering || relayoutQueued || !IsHandleCreated || IsDisposed)
                return;

            relayoutQueued = true;
            BeginInvoke(new MethodInvoker(delegate
            {
                relayoutQueued = false;
                if (!IsDisposed)
                    RenderPage();
            }));
        }

        private int MeasurePageSize()
        {
            if (!list.IsHandleCreated)
                return 1;

            int nativeCount = SendMessage(list.Handle, ItemsPerPageMessage, IntPtr.Zero, IntPtr.Zero).ToInt32();
            return Math.Max(1, Math.Min(MaxRenderedRows, nativeCount));
        }

        private void RenderPage()
        {
            rendering = true;
            try
            {
                Array.Clear(visibleRows, 0, visibleRows.Length);
                pageSize = MeasurePageSize();
                firstVisibleIndex = Math.Max(0, Math.Min(firstVisibleIndex, LastPageStart()));
                scrollBar.LargeChange = pageSize;
                scrollBar.Maximum = Math.Max(0, entries.Count - 1);
                scrollBar.Visible = entries.Count > pageSize;

                int available = Math.Max(100, list.ClientSize.Width - priorityWidth - timeWidth - 3);
                if (eventColumn.Width != available)
                    eventColumn.Width = available;

                int visibleCount = Math.Min(pageSize, entries.Count - firstVisibleIndex);
                if (list.VirtualListSize != visibleCount)
                    list.VirtualListSize = visibleCount;
                RefreshRows();
            }
            finally
            {
                rendering = false;
            }
        }

        private void RefreshRows()
        {
            if (scrollBar.Value != firstVisibleIndex)
                scrollBar.Value = firstVisibleIndex;

            int localSelection = selectedIndex - firstVisibleIndex;
            if (localSelection < 0 || localSelection >= list.VirtualListSize)
                localSelection = -1;
            int selectedRow = list.SelectedIndices.Count == 0 ? -1 : list.SelectedIndices[0];
            if (selectedRow != localSelection)
            {
                list.SelectedIndices.Clear();
                if (localSelection >= 0)
                {
                    list.SelectedIndices.Add(localSelection);
                    list.Items[localSelection].Focused = true;
                }
            }
            list.Invalidate();
        }

        private void RetrieveRow(object sender, RetrieveVirtualItemEventArgs args)
        {
            // Windows only sees the current screenful as a list. The scrollbar maps it into the archive.
            if (args.ItemIndex < 0 || args.ItemIndex >= visibleRows.Length ||
                args.ItemIndex >= entries.Count - firstVisibleIndex)
            {
                // Windows can request an old row while the list is shrinking.
                args.Item = new ListViewItem(new string[list.Columns.Count]);
                return;
            }

            ListViewItem row = visibleRows[args.ItemIndex];
            if (row == null)
            {
                VaultEvent entry = entries[firstVisibleIndex + args.ItemIndex];
                row = new ListViewItem(new[]
                {
                    "● " + VaultForm.PriorityName(entry.Severity),
                    entry.Title,
                    VaultForm.FormatTime(entry.UtcTicks, "g")
                });
                if (!SystemInformation.HighContrast)
                {
                    row.UseItemStyleForSubItems = false;
                    row.SubItems[0].ForeColor = VaultForm.PriorityColor(entry.Severity);
                }
                visibleRows[args.ItemIndex] = row;
            }
            args.Item = row;
        }


        private int LastPageStart()
        {
            return Math.Max(0, entries.Count - pageSize);
        }

        private void ScrollTo(int nextIndex)
        {
            nextIndex = Math.Max(0, Math.Min(nextIndex, LastPageStart()));
            if (nextIndex == firstVisibleIndex)
                return;

            firstVisibleIndex = nextIndex;
            rendering = true;
            try
            {
                Array.Clear(visibleRows, 0, visibleRows.Length);
                RefreshRows();
            }
            finally
            {
                rendering = false;
            }
        }

        private void ScrollWheel(int delta)
        {
            wheelRemainder += delta;
            int notches = wheelRemainder / WheelDelta;
            wheelRemainder -= notches * WheelDelta;
            int lines = SystemInformation.MouseWheelScrollLines;
            ScrollTo(firstVisibleIndex - notches * (lines < 0 ? pageSize : lines));
        }

        private void ReadRowSelection(object sender, EventArgs args)
        {
            if (rendering || list.SelectedIndices.Count == 0)
                return;

            SetSelection(firstVisibleIndex + list.SelectedIndices[0]);
        }

        private void SetSelection(int index)
        {
            if (index == selectedIndex)
                return;

            selectedIndex = index;
            EventHandler changed = SelectedIndexChanged;
            if (changed != null)
                changed(this, EventArgs.Empty);
        }

        private bool SelectByKey(Keys key)
        {
            if (entries.Count == 0)
                return false;

            int index = selectedIndex < 0 ? firstVisibleIndex : selectedIndex;
            switch (key)
            {
                case Keys.Up: index--; break;
                case Keys.Down: index++; break;
                case Keys.PageUp: index -= pageSize; break;
                case Keys.PageDown: index += pageSize; break;
                case Keys.Home: index = 0; break;
                case Keys.End: index = entries.Count - 1; break;
                default: return false;
            }

            index = Math.Max(0, Math.Min(index, entries.Count - 1));
            if (index < firstVisibleIndex)
                ScrollTo(index);
            else if (index >= firstVisibleIndex + pageSize)
                ScrollTo(index - pageSize + 1);

            int localIndex = index - firstVisibleIndex;
            list.Items[localIndex].Focused = true;
            list.Items[localIndex].Selected = true;
            SetSelection(index);
            return true;
        }

        private sealed class ViewportList : ListView
        {
            private readonly EventViewport owner;

            public ViewportList(EventViewport owner)
            {
                this.owner = owner;
                DoubleBuffered = true;
            }

            protected override bool ProcessCmdKey(ref Message message, Keys keyData)
            {
                return owner.SelectByKey(keyData) || base.ProcessCmdKey(ref message, keyData);
            }

            protected override void OnMouseWheel(MouseEventArgs args)
            {
                owner.ScrollWheel(args.Delta);
            }
        }
    }
}
