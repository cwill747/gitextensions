﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using GitCommands;
using GitCommands.Git;
using GitUI.Hotkey;
using GitUI.Script;
using GitUI.Tag;
using Gravatar;
using ResourceManager.Translation;
using System.DirectoryServices;

namespace GitUI
{
    public enum RevisionGridLayout
    {
        FilledBranchesSmall = 1,
        FilledBranchesSmallWithGraph = 2,
        Small = 3,
        SmallWithGraph = 4,
        Card = 5,
        CardWithGraph = 6,
        LargeCard = 7,
        LargeCardWithGraph = 8
    }

    [DefaultEvent("DoubleClick")]
    public sealed partial class RevisionGrid : GitExtensionsControl
    {
        private readonly IndexWatcher _indexWatcher = new IndexWatcher();
        private readonly TranslationString _currentWorkingDirChanges = new TranslationString("Current uncommitted changes");
        private readonly TranslationString _currentIndex = new TranslationString("Commit index");
        private readonly TranslationString _areYouSureYouWantCheckout = new TranslationString("Are you sure to checkout the selected revision?");
        private readonly TranslationString _areYouSureYouWantCheckoutCaption = new TranslationString("Checkout revision");
        private readonly TranslationString _droppingFilesBlocked = new TranslationString("For you own protection dropping more than 10 patch files at once is blocked!");

        private const int NODE_DIMENSION = 8;
        private const int LANE_WIDTH = 13;
        private const int LANE_LINE_WIDTH = 2;
        private Brush selectedItemBrush;

        private readonly FormRevisionFilter _revisionFilter = new FormRevisionFilter();

        private readonly SynchronizationContext _syncContext;
        public string LogParam = "HEAD --all --boundary";

        private bool _initialLoad = true;
        private string _initialSelectedRevision;
        private string _lastQuickSearchString = string.Empty;
        private Label _quickSearchLabel;
        private string _quickSearchString;
        private RevisionGraph _revisionGraphCommand;

        private RevisionGridLayout layout;
        private int rowHeigth;

        public RevisionGrid()
        {
            _syncContext = SynchronizationContext.Current;

            InitLayout();
            InitializeComponent();
            this.Loading.Image = global::GitUI.Properties.Resources.loadingpanel;

            Translate();

            NormalFont = SystemFonts.DefaultFont;
            Loading.Paint += Loading_Paint;

            Revisions.CellPainting += RevisionsCellPainting;
            Revisions.CellFormatting += RevisionsCellFormatting;
            Revisions.KeyDown += RevisionsKeyDown;

            showAuthorDateToolStripMenuItem.Checked = Settings.ShowAuthorDate;
            orderRevisionsByDateToolStripMenuItem.Checked = Settings.OrderRevisionByDate;
            showRelativeDateToolStripMenuItem.Checked = Settings.RelativeDate;
            drawNonrelativesGrayToolStripMenuItem.Checked = Settings.RevisionGraphDrawNonRelativesGray;
            showGitNotesToolStripMenuItem.Checked = Settings.ShowGitNotes;

            BranchFilter = String.Empty;
            SetShowBranches();
            Filter = "";
            FixedFilter = "";
            InMemFilterIgnoreCase = false;
            InMemAuthorFilter = "";
            InMemCommitterFilter = "";
            InMemMessageFilter = "";
            AllowGraphWithFilter = false;
            _quickSearchString = "";
            quickSearchTimer.Tick += QuickSearchTimerTick;

            Revisions.Loading += RevisionsLoading;

            //Allow to drop patch file on revisiongrid
            Revisions.DragEnter += Revisions_DragEnter;
            Revisions.DragDrop += Revisions_DragDrop;
            Revisions.AllowDrop = true;
            Revisions.ColumnHeadersVisible = false;

            this.HotkeysEnabled = true;
            try
            {
                SetRevisionsLayout((RevisionGridLayout)Settings.RevisionGraphLayout);
            }
            catch
            {
                SetRevisionsLayout(RevisionGridLayout.SmallWithGraph);
            }
        }

        void Loading_Paint(object sender, PaintEventArgs e)
        {
            // If our loading state has changed since the last paint, update it.
            if (Loading != null)
            {
                if (Loading.Visible != _isLoading)
                {
                    Loading.Visible = _isLoading;
                }
            }
        }

        [Browsable(false)]
        public Font HeadFont { get; private set; }
        [Browsable(false)]
        public Font SuperprojectFont { get; private set; }
        [Browsable(false)]
        public int LastScrollPos { get; private set; }
        [Browsable(false)]
        public IComparable[] LastSelectedRows { get; private set; }
        [Browsable(false)]
        public Font RefsFont { get; private set; }
        private Font _normalFont;
        [Category("Appearance")]
        public Font NormalFont
        {
            get { return _normalFont; }
            set
            {
                _normalFont = value;
                Message.DefaultCellStyle.Font = _normalFont;
                Date.DefaultCellStyle.Font = _normalFont;

                RefsFont = IsFilledBranchesLayout() ? _normalFont : new Font(_normalFont, FontStyle.Bold);
                HeadFont = new Font(_normalFont, FontStyle.Bold);
                SuperprojectFont = new Font(_normalFont, FontStyle.Underline);
            }
        }

        [Category("Filter")]
        public string Filter { get; set; }
        [Category("Filter")]
        public string FixedFilter { get; set; }
        [Category("Filter")]
        [DefaultValue(false)]
        public bool InMemFilterIgnoreCase { get; set; }
        [Category("Filter")]
        public string InMemAuthorFilter { get; set; }
        [Category("Filter")]
        public string InMemCommitterFilter { get; set; }
        [Category("Filter")]
        public string InMemMessageFilter { get; set; }
        [Category("Filter")]
        public string BranchFilter { get; set; }
        [Category("Filter")]
        [DefaultValue(false)]
        public bool AllowGraphWithFilter { get; set; }

        [Browsable(false)]
        public string CurrentCheckout { get; set; }
        [Browsable(false)]
        public string SuperprojectCurrentCheckout { get; set; }
        [Browsable(false)]
        public int LastRow { get; set; }

        [Description("Indicates whether the user is allowed to select more than one commit at a time.")]
        [Category("Behavior")]
        [DefaultValue(true)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        public bool MultiSelect
        {
            get { return Revisions.MultiSelect; }
            set { Revisions.MultiSelect = value; }
        }

        public void SetInitialRevision(GitRevision initialSelectedRevision)
        {
            _initialSelectedRevision = initialSelectedRevision != null ? initialSelectedRevision.Guid : null;
        }

        public event EventHandler ActionOnRepositoryPerformed;

        private void OnActionOnRepositoryPerformed()
        {
            if (ActionOnRepositoryPerformed != null)
                ActionOnRepositoryPerformed(this, null);
        }

        private bool _isLoading;
        private void RevisionsLoading(bool isLoading)
        {
            // Since this can happen on a background thread, we'll just set a
            // flag and deal with it next time we paint (a bit of a hack, but
            // it works)
            _isLoading = isLoading;
        }

        private void ShowQuickSearchString()
        {
            if (_quickSearchLabel == null)
            {
                _quickSearchLabel
                    = new Label
                          {
                              Location = new Point(10, 10),
                              BorderStyle = BorderStyle.FixedSingle,
                              ForeColor = SystemColors.InfoText,
                              BackColor = SystemColors.Info
                          };
                Controls.Add(_quickSearchLabel);
            }

            _quickSearchLabel.Visible = true;
            _quickSearchLabel.BringToFront();
            _quickSearchLabel.Text = _quickSearchString;
            _quickSearchLabel.AutoSize = true;
        }

        private void HideQuickSearchString()
        {
            if (_quickSearchLabel != null)
                _quickSearchLabel.Visible = false;
        }

        private void QuickSearchTimerTick(object sender, EventArgs e)
        {
            quickSearchTimer.Stop();
            _quickSearchString = "";
            HideQuickSearchString();
        }

        private void RestartQuickSearchTimer()
        {
            quickSearchTimer.Stop();
            quickSearchTimer.Interval = Settings.RevisionGridQuickSearchTimeout;
            quickSearchTimer.Start();
        }

        private void RevisionsKeyDown(object sender, KeyEventArgs e)
        {
            var curIndex = -1;
            if (Revisions.SelectedRows.Count > 0)
                curIndex = Revisions.SelectedRows[0].Index;
            if (e.Alt && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
            {
                RestartQuickSearchTimer();

                bool reverse = e.KeyCode == Keys.Up;
                var nextIndex = 0;
                if (curIndex >= 0)
                    nextIndex = reverse ? curIndex - 1 : curIndex + 1;
                _quickSearchString = _lastQuickSearchString;
                FindNextMatch(nextIndex, _quickSearchString, reverse);
                ShowQuickSearchString();
                e.Handled = true;
                return;
            }

            curIndex = curIndex >= 0 ? curIndex : 0;
            int key = e.KeyValue;
            if (!e.Alt && !e.Control && key == 8 && _quickSearchString.Length > 1) //backspace
            {
                RestartQuickSearchTimer();

                _quickSearchString = _quickSearchString.Substring(0, _quickSearchString.Length - 1);

                FindNextMatch(curIndex, _quickSearchString, false);
                _lastQuickSearchString = _quickSearchString;

                e.Handled = true;
                ShowQuickSearchString();
            }
            else if (!e.Alt && !e.Control && (char.IsLetterOrDigit((char)key) || char.IsNumber((char)key) || char.IsSeparator((char)key) || key == 191))
            {
                RestartQuickSearchTimer();

                //The code below is meant to fix the weird keyvalues when pressing keys e.g. ".".
                switch (key)
                {
                    case 51:
                        _quickSearchString = e.Shift ? string.Concat(_quickSearchString, "#").ToLower() : string.Concat(_quickSearchString, "3").ToLower();
                        break;
                    case 188:
                        _quickSearchString = string.Concat(_quickSearchString, ",").ToLower();
                        break;
                    case 189:
                        _quickSearchString = e.Shift ? string.Concat(_quickSearchString, "_").ToLower() : string.Concat(_quickSearchString, "-").ToLower();
                        break;
                    case 190:
                        _quickSearchString = string.Concat(_quickSearchString, ".").ToLower();
                        break;
                    case 191:
                        _quickSearchString = string.Concat(_quickSearchString, "/").ToLower();
                        break;
                    default:
                        _quickSearchString = string.Concat(_quickSearchString, (char)e.KeyValue).ToLower();
                        break;
                }

                FindNextMatch(curIndex, _quickSearchString, false);
                _lastQuickSearchString = _quickSearchString;

                e.Handled = true;
                ShowQuickSearchString();
            }
            else
            {
                _quickSearchString = "";
                HideQuickSearchString();
                e.Handled = false;
            }
        }

        private void FindNextMatch(int startIndex, string searchString, bool reverse)
        {
            if (Revisions.RowCount == 0)
                return;

            var searchResult =
                reverse
                    ? SearchInReverseOrder(startIndex, searchString)
                    : SearchForward(startIndex, searchString);

            if (!searchResult.HasValue)
                return;

            Revisions.ClearSelection();
            Revisions.Rows[searchResult.Value].Selected = true;

            Revisions.CurrentCell = Revisions.Rows[searchResult.Value].Cells[1];
        }

        private int? SearchForward(int startIndex, string searchString)
        {
            // Check for out of bounds roll over if required
            int index;
            if (startIndex < 0 || startIndex >= Revisions.RowCount)
                startIndex = 0;

            for (index = startIndex; index < Revisions.RowCount; ++index)
            {
                if (GetRevision(index).MatchesSearchString(searchString))
                    return index;
            }

            // We didn't find it so start searching from the top
            for (index = 0; index < startIndex; ++index)
            {
                if (GetRevision(index).MatchesSearchString(searchString))
                    return index;
            }

            return null;
        }

        private int? SearchInReverseOrder(int startIndex, string searchString)
        {
            // Check for out of bounds roll over if required
            int index;
            if (startIndex < 0 || startIndex >= Revisions.RowCount)
                startIndex = Revisions.RowCount - 1;

            for (index = startIndex; index >= 0; --index)
            {
                if (GetRevision(index).MatchesSearchString(searchString))
                    return index;
            }

            // We didn't find it so start searching from the bottom
            for (index = Revisions.RowCount - 1; index > startIndex; --index)
            {
                if (GetRevision(index).MatchesSearchString(searchString))
                    return index;
            }


            return null;
        }

        public void DisableContextMenu()
        {
            Revisions.ContextMenuStrip = null;
        }

        public void FormatQuickFilter(string filter,
                                      bool[] parameters,
                                      out string revListArgs,
                                      out string inMemMessageFilter,
                                      out string inMemCommitterFilter,
                                      out string inMemAuthorFilter)
        {
            revListArgs = string.Empty;
            inMemMessageFilter = string.Empty;
            inMemCommitterFilter = string.Empty;
            inMemAuthorFilter = string.Empty;
            if (!string.IsNullOrEmpty(filter))
            {
                // hash filtering only possible in memory
                var cmdLineSafe = GitCommandHelpers.VersionInUse.IsRegExStringCmdPassable(filter);
                revListArgs = " --regexp-ignore-case ";
                if (parameters[0])
                    if (cmdLineSafe)
                        revListArgs += "--grep=\"" + filter + "\" ";
                    else
                        inMemMessageFilter = filter;
                if (parameters[1])
                    if (cmdLineSafe)
                        revListArgs += "--committer=\"" + filter + "\" ";
                    else
                        inMemCommitterFilter = filter;
                if (parameters[2])
                    if (cmdLineSafe)
                        revListArgs += "--author=\"" + filter + "\" ";
                    else
                        inMemAuthorFilter = filter;
                if (parameters[3])
                    if (cmdLineSafe)
                        revListArgs += "\"-S" + filter + "\" ";
                    else
                        throw new InvalidOperationException("Filter text not valid for \"Diff contains\" filter.");
            }
        }

        public bool SetAndApplyBranchFilter(string filter)
        {
            if (filter.Equals(_revisionFilter.GetBranchFilter()))
                return false;
            if (filter.Equals(""))
            {
                Settings.BranchFilterEnabled = false;
                Settings.ShowCurrentBranchOnly = true;
            }
            else
            {
                Settings.BranchFilterEnabled = true;
                Settings.ShowCurrentBranchOnly = false;
                _revisionFilter.SetBranchFilter(filter);
            }
            SetShowBranches();
            return true;
        }

        public void SetLimit(int limit)
        {
            _revisionFilter.SetLimit(limit);
        }

        public override void Refresh()
        {
            SetRevisionsLayout();

            base.Refresh();

            Revisions.Refresh();
        }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();

            _isLoading = true;
            Error.Visible = false;
            NoCommits.Visible = false;
            NoGit.Visible = false;
            Revisions.Visible = false;
            Loading.Visible = true;
            Loading.BringToFront();
        }

        public new void Load()
        {
            if (!DesignMode)
                ReloadHotkeys();
            ForceRefreshRevisions();
        }

        public event EventHandler SelectionChanged;

        public void SetSelectedIndex(int index)
        {
            if (Revisions.Rows[index].Selected)
                return;

            Revisions.ClearSelection();

            Revisions.Rows[index].Selected = true;
            Revisions.CurrentCell = Revisions.Rows[index].Cells[1];

            Revisions.Select();
        }

        public void SetSelectedRevision(GitRevision revision)
        {
            if (revision != null)
            {
                for (var i = 0; i < Revisions.RowCount; i++)
                {
                    if (GetRevision(i).Guid == revision.Guid)
                    {
                        SetSelectedIndex(i);
                        return;
                    }
                }
            }

            Revisions.ClearSelection();
            Revisions.Select();
        }

        private void RevisionsSelectionChanged(object sender, EventArgs e)
        {
            if (Revisions.SelectedRows.Count > 0)
                LastRow = Revisions.SelectedRows[0].Index;

            SelectionTimer.Enabled = false;
            SelectionTimer.Stop();
            SelectionTimer.Enabled = true;
            SelectionTimer.Start();
        }

        public List<GitRevision> GetSelectedRevisions()
        {
            return GetSelectedRevisions(null);
        }

        public List<GitRevision> GetSelectedRevisions(SortDirection? direction)
        {
            var rows = Revisions
                .SelectedRows
                .Cast<DataGridViewRow>()
                .Where(row => Revisions.RowCount > row.Index);

            
            if (direction.HasValue)
            {
                int d = direction.Value == SortDirection.Ascending ? 1 : -1;
                rows = rows.OrderBy((row) => row.Index, (r1, r2) => d * (r1 - r2));
            }

            return rows
                .Select(row => GetRevision(row.Index))
                .ToList();
        }

        public GitRevision GetRevision(int aRow)
        {
            return Revisions.GetRowData(aRow);
        }

        public GitRevision GetCurrentRevision()
        {
            const string formatString =
                /* Tree           */ "%T%n" +
                /* Author Name    */ "%aN%n" +
                /* Author Date    */ "%ai%n" +
                /* Committer Name */ "%cN%n" +
                /* Committer Date */ "%ci%n" +
                /* Commit Message */ "%s";
            string cmd = "log -n 1 --pretty=format:" + formatString + " " + CurrentCheckout;
            var RevInfo = Settings.Module.RunGitCmd(cmd);
            string[] Infos = RevInfo.Split('\n');
            var Revision = new GitRevision(CurrentCheckout)
            {
                TreeGuid = Infos[0],
                Author = Infos[1],
                Committer = Infos[3],
                Message = Infos[5]
            };
            DateTime date;
            DateTime.TryParse(Infos[2], out date);
            Revision.AuthorDate = date;
            DateTime.TryParse(Infos[4], out date);
            Revision.CommitDate = date;
            List<GitHead> heads = Settings.Module.GetHeads(true, true);
            foreach (GitHead head in heads)
            {
                if (head.Guid.Equals(Revision.Guid))
                    Revision.Heads.Add(head);
            }
            return Revision;
        }

        public void RefreshRevisions()
        {
            if (IndexWatcher.IndexChanged)
                ForceRefreshRevisions();
        }

        private class RevisionGraphInMemFilterOr : RevisionGraphInMemFilter
        {
            private RevisionGraphInMemFilter fFilter1;
            private RevisionGraphInMemFilter fFilter2;
            public RevisionGraphInMemFilterOr(RevisionGraphInMemFilter aFilter1,
                                              RevisionGraphInMemFilter aFilter2)
            {
                fFilter1 = aFilter1;
                fFilter2 = aFilter2;
            }

            public override bool PassThru(GitRevision rev)
            {
                return fFilter1.PassThru(rev) || fFilter2.PassThru(rev);
            }
        }

        private class RevisionGridInMemFilter : RevisionGraphInMemFilter
        {
            private readonly bool _IgnoreCase;
            private readonly string _AuthorFilter;
            private readonly Regex _AuthorFilterRegex;
            private readonly string _CommitterFilter;
            private readonly Regex _CommitterFilterRegex;
            private readonly string _MessageFilter;
            private readonly Regex _MessageFilterRegex;

            public RevisionGridInMemFilter(string authorFilter, string committerFilter, string messageFilter, bool ignoreCase)
            {
                _IgnoreCase = ignoreCase;
                SetUpVars(authorFilter, ref _AuthorFilter, ref _AuthorFilterRegex);
                SetUpVars(committerFilter, ref _CommitterFilter, ref _CommitterFilterRegex);
                SetUpVars(messageFilter, ref _MessageFilter, ref _MessageFilterRegex);
            }

            private void SetUpVars(string filterValue,
                                   ref string filterStr,
                                   ref Regex filterRegEx)
            {
                RegexOptions opts = RegexOptions.None;
                if (_IgnoreCase) opts = opts | RegexOptions.IgnoreCase;
                filterStr = filterValue != null ? filterValue.Trim() : string.Empty;
                try
                {
                    filterRegEx = new Regex(filterStr, opts);
                }
                catch (ArgumentException)
                {
                    filterRegEx = null;
                }
            }

            private static bool CheckCondition(string filter, Regex regex, string value)
            {
                return string.IsNullOrEmpty(filter) ||
                       ((regex != null) && regex.Match(value).Success);
            }

            public override bool PassThru(GitRevision rev)
            {
                return CheckCondition(_AuthorFilter, _AuthorFilterRegex, rev.Author) &&
                       CheckCondition(_CommitterFilter, _CommitterFilterRegex, rev.Committer) &&
                       CheckCondition(_MessageFilter, _MessageFilterRegex, rev.Message);
            }

            public static RevisionGridInMemFilter CreateIfNeeded(string authorFilter,
                                                                 string committerFilter,
                                                                 string messageFilter,
                                                                 bool ignoreCase)
            {
                if (!(string.IsNullOrEmpty(authorFilter) &&
                      string.IsNullOrEmpty(committerFilter) &&
                      string.IsNullOrEmpty(messageFilter)))
                    return new RevisionGridInMemFilter(authorFilter,
                                                       committerFilter,
                                                       messageFilter,
                                                       ignoreCase);
                else
                    return null;
            }
        }

        public void ReloadHotkeys()
        {
            this.Hotkeys = HotkeySettingsManager.LoadHotkeys(HotkeySettingsName);
        }

        public void ReloadTranslation()
        {
            Translate();
        }

        public void ForceRefreshRevisions()
        {
            try
            {
                ApplyFilterFromRevisionFilterDialog();

                _initialLoad = true;

                LastScrollPos = Revisions.FirstDisplayedScrollingRowIndex;

                DisposeRevisionGraphCommand();

                var newCurrentCheckout = Settings.Module.GetCurrentCheckout();
                var newSuperprojectCurrentCheckout = Settings.Module.GetSuperprojectCurrentCheckout();

                // If the current checkout changed, don't get the currently selected rows, select the
                // new current checkout instead.
                if (newCurrentCheckout == CurrentCheckout)
                {
                    LastSelectedRows = Revisions.SelectedIds;
                }
                else
                {
                    // This is a new checkout, so ensure the variable is cleared out.
                    LastSelectedRows = null;
                }

                Revisions.ClearSelection();
                CurrentCheckout = newCurrentCheckout;
                SuperprojectCurrentCheckout = newSuperprojectCurrentCheckout;
                Revisions.Clear();
                Error.Visible = false;

                if (!Settings.Module.ValidWorkingDir())
                {
                    Revisions.Visible = false;
                    NoCommits.Visible = true;
                    Loading.Visible = false;
                    NoGit.Visible = true;
                    string dir = Settings.Module.WorkingDir;
                    if (String.IsNullOrEmpty(dir) || !Directory.Exists(dir) ||
                        Directory.GetDirectories(dir).Length == 0 &&
                        Directory.GetFiles(dir).Length == 0)
                        CloneRepository.Show();
                    else
                        CloneRepository.Hide();
                    NoGit.BringToFront();
                    return;
                }

                NoCommits.Visible = false;
                NoGit.Visible = false;
                Revisions.Visible = true;
                Revisions.BringToFront();
                Revisions.Enabled = false;
                Loading.Visible = true;
                Loading.BringToFront();
                _isLoading = true;
                base.Refresh();

                IndexWatcher.Reset();

                if (!Settings.ShowGitNotes && !LogParam.Contains(" --not --glob=notes --not"))
                    LogParam = LogParam + " --not --glob=notes --not";

                if (Settings.ShowGitNotes && LogParam.Contains(" --not --glob=notes --not"))
                    LogParam = LogParam.Replace("  --not --glob=notes --not", string.Empty);

                RevisionGridInMemFilter revisionFilterIMF = RevisionGridInMemFilter.CreateIfNeeded(_revisionFilter.GetInMemAuthorFilter(),
                                                                                                   _revisionFilter.GetInMemCommitterFilter(),
                                                                                                   _revisionFilter.GetInMemMessageFilter(),
                                                                                                   _revisionFilter.GetIgnoreCase());
                RevisionGridInMemFilter filterBarIMF = RevisionGridInMemFilter.CreateIfNeeded(InMemAuthorFilter,
                                                                                              InMemCommitterFilter,
                                                                                              InMemMessageFilter,
                                                                                              InMemFilterIgnoreCase);
                RevisionGraphInMemFilter revGraphIMF;
                if (revisionFilterIMF != null && filterBarIMF != null)
                    revGraphIMF = new RevisionGraphInMemFilterOr(revisionFilterIMF, filterBarIMF);
                else if (revisionFilterIMF != null)
                    revGraphIMF = revisionFilterIMF;
                else
                    revGraphIMF = filterBarIMF;

                _revisionGraphCommand = new RevisionGraph { BranchFilter = BranchFilter, LogParam = LogParam + _revisionFilter.GetFilter() + Filter + FixedFilter };
                _revisionGraphCommand.Updated += GitGetCommitsCommandUpdated;
                _revisionGraphCommand.Exited += GitGetCommitsCommandExited;
                _revisionGraphCommand.Error += _revisionGraphCommand_Error;
                _revisionGraphCommand.InMemFilter = revGraphIMF;
                //_revisionGraphCommand.BeginUpdate += ((s, e) => Revisions.Invoke((Action) (() => Revisions.Clear())));
                _revisionGraphCommand.Execute();
                LoadRevisions();
                SetRevisionsLayout();
            }
            catch (Exception exception)
            {
                Error.Visible = true;
                Error.BringToFront();
                MessageBox.Show(this, exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void _revisionGraphCommand_Error(object sender, EventArgs e)
        {
            // This has to happen on the UI thread
            _syncContext.Send(o =>
                                  {
                                      Error.Visible = true;
                                      //Error.BringToFront();
                                      NoGit.Visible = false;
                                      NoCommits.Visible = false;
                                      Revisions.Visible = false;
                                      Loading.Visible = false;
                                  }, this);
        }

        private void GitGetCommitsCommandUpdated(object sender, EventArgs e)
        {
            var updatedEvent = (RevisionGraph.RevisionGraphUpdatedEventArgs)e;
            UpdateGraph(updatedEvent.Revision);
        }

        private bool FilterIsApplied(bool inclBranchFilter)
        {
            return (inclBranchFilter && !string.IsNullOrEmpty(BranchFilter)) ||
                   !(string.IsNullOrEmpty(Filter) &&
                     !_revisionFilter.FilterEnabled() &&
                     string.IsNullOrEmpty(InMemAuthorFilter) &&
                     string.IsNullOrEmpty(InMemCommitterFilter) &&
                     string.IsNullOrEmpty(InMemMessageFilter));
        }

        private bool ShouldHideGraph(bool inclBranchFilter)
        {
            return (inclBranchFilter && !string.IsNullOrEmpty(BranchFilter)) ||
                   !(!_revisionFilter.ShouldHideGraph() &&
                     string.IsNullOrEmpty(InMemAuthorFilter) &&
                     string.IsNullOrEmpty(InMemCommitterFilter) &&
                     string.IsNullOrEmpty(InMemMessageFilter));
        }

        private void DisposeRevisionGraphCommand()
        {
            if (_revisionGraphCommand != null)
            {
                //Dispose command, it is not needed anymore
                _revisionGraphCommand.Updated -= GitGetCommitsCommandUpdated;
                _revisionGraphCommand.Exited -= GitGetCommitsCommandExited;
                _revisionGraphCommand.Error -= _revisionGraphCommand_Error;

                _revisionGraphCommand.Dispose();
                _revisionGraphCommand = null;
            }
        }

        private void GitGetCommitsCommandExited(object sender, EventArgs e)
        {
            _isLoading = false;

            if (_revisionGraphCommand.RevisionCount == 0 &&
                !FilterIsApplied(true))
            {
                // This has to happen on the UI thread
                _syncContext.Send(o =>
                                      {
                                          NoGit.Visible = false;
                                          NoCommits.Visible = true;
                                          //NoCommits.BringToFront();
                                          Revisions.Visible = false;
                                          Loading.Visible = false;
                                      }, this);
            }
            else
            {
                // This has to happen on the UI thread
                _syncContext.Send(o =>
                                      {
                                          UpdateGraph(null);
                                          Loading.Visible = false;
                                          SelectInitialRevision();
                                          _isLoading = false;
                                      }, this);
            }
        }

        private void SelectInitialRevision()
        {
            if (string.IsNullOrEmpty(_initialSelectedRevision) || Revisions.SelectedRows.Count != 0)
                return;

            for (var i = 0; i < Revisions.RowCount; i++)
            {
                if (GetRevision(i).Guid == _initialSelectedRevision)
                    SetSelectedIndex(i);
            }
        }

        private static string GetDateHeaderText()
        {
            return Settings.ShowAuthorDate ? Strings.GetAuthorDateText() : Strings.GetCommitDateText();
        }

        private void LoadRevisions()
        {
            if (_revisionGraphCommand == null)
            {
                return;
            }

            Revisions.SuspendLayout();

            Revisions.Columns[1].HeaderText = Strings.GetMessageText();
            Revisions.Columns[2].HeaderText = Strings.GetAuthorText();
            Revisions.Columns[3].HeaderText = GetDateHeaderText();

            Revisions.SelectionChanged -= RevisionsSelectionChanged;

            if (LastSelectedRows != null)
            {
                Revisions.SelectedIds = LastSelectedRows;
                LastSelectedRows = null;
            }
            else if (_initialSelectedRevision == null)
            {
                Revisions.SelectedIds = new IComparable[] { CurrentCheckout };
            }

            if (LastScrollPos > 0 && Revisions.RowCount > LastScrollPos)
            {
                Revisions.FirstDisplayedScrollingRowIndex = LastScrollPos;
                LastScrollPos = -1;
            }

            Revisions.Enabled = true;
            Revisions.Focus();
            Revisions.SelectionChanged += RevisionsSelectionChanged;

            Revisions.ResumeLayout();

            if (!_initialLoad)
                return;

            _initialLoad = false;
            SelectionTimer.Enabled = false;
            SelectionTimer.Stop();
            SelectionTimer.Enabled = true;
            SelectionTimer.Start();
        }

        private void RevisionsCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            // If our loading state has changed since the last paint, update it.
            if (Loading != null)
            {
                if (Loading.Visible != _isLoading)
                {
                    Loading.Visible = _isLoading;
                }
            }

            // The graph column is handled by the DvcsGraph
            if (e.ColumnIndex == 0)
            {
                return;
            }

            var column = e.ColumnIndex;
            if (e.RowIndex < 0 || (e.State & DataGridViewElementStates.Visible) == 0)
                return;

            if (Revisions.RowCount <= e.RowIndex)
                return;

            var revision = GetRevision(e.RowIndex);
            if (revision == null)
                return;

            e.Handled = true;

            bool isRowSelected = ((e.State & DataGridViewElementStates.Selected) == DataGridViewElementStates.Selected);

            if (isRowSelected /*&& !showRevisionCards*/)
                e.Graphics.FillRectangle(selectedItemBrush, e.CellBounds);
            else
                e.Graphics.FillRectangle(new SolidBrush(Color.White), e.CellBounds);

            Color foreColor;

            if (!Settings.RevisionGraphDrawNonRelativesGray || !Settings.RevisionGraphDrawNonRelativesTextGray || Revisions.RowIsRelative(e.RowIndex))
            {
                foreColor = isRowSelected && IsFilledBranchesLayout()
                    ? SystemColors.HighlightText
                    : e.CellStyle.ForeColor;
            }
            else
            {
                foreColor = Color.LightGray;
            }

            Brush foreBrush = new SolidBrush(foreColor);
            var rowFont = NormalFont;
            if (revision.Guid == CurrentCheckout /*&& !showRevisionCards*/)
                rowFont = HeadFont;
            else if (revision.Guid == SuperprojectCurrentCheckout)
                rowFont = SuperprojectFont;

            switch (column)
            {
                case 1: //Description!!
                    {
                        int baseOffset = 0;
                        if (IsCardLayout())
                        {
                            baseOffset = 5;

                            Rectangle cellRectangle = new Rectangle(e.CellBounds.Left + baseOffset, e.CellBounds.Top + 1, e.CellBounds.Width - (baseOffset * 2), e.CellBounds.Height - 4);

                            if (!Settings.RevisionGraphDrawNonRelativesGray || Revisions.RowIsRelative(e.RowIndex))
                            {
                                e.Graphics.FillRectangle(
                                    new LinearGradientBrush(cellRectangle,
                                                            Color.FromArgb(255, 220, 220, 231),
                                                            Color.FromArgb(255, 240, 240, 250), 90, false), cellRectangle);
                                e.Graphics.DrawRectangle(new Pen(Color.FromArgb(255, 200, 200, 200), 1), cellRectangle);
                            }
                            else
                            {
                                e.Graphics.FillRectangle(
                                    new LinearGradientBrush(cellRectangle,
                                                            Color.FromArgb(255, 240, 240, 240),
                                                            Color.FromArgb(255, 250, 250, 250), 90, false), cellRectangle);
                            }

                            if ((e.State & DataGridViewElementStates.Selected) == DataGridViewElementStates.Selected)
                                e.Graphics.DrawRectangle(new Pen(Revisions.RowTemplate.DefaultCellStyle.SelectionBackColor, 1), cellRectangle);
                        }

                        float offset = baseOffset;
                        var heads = revision.Heads;

                        if (heads.Count > 0)
                        {
                            heads.Sort((left, right) =>
                                           {
                                               if (left.IsTag != right.IsTag)
                                                   return right.IsTag.CompareTo(left.IsTag);
                                               if (left.IsRemote != right.IsRemote)
                                                   return left.IsRemote.CompareTo(right.IsRemote);
                                               return left.Name.CompareTo(right.Name);
                                           });

                            foreach (var head in heads)
                            {
                                if ((head.IsRemote && !ShowRemoteBranches.Checked))
                                    continue;

                                Font refsFont;

                                if (IsFilledBranchesLayout())
                                {
                                    //refsFont = head.Selected ? rowFont : new Font(rowFont, FontStyle.Regular);
                                    refsFont = rowFont;

                                    //refsFont = head.Selected
                                    //    ? new Font(rowFont, rowFont.Style | FontStyle.Italic)
                                    //    : rowFont;
                                }
                                else
                                {
                                    refsFont = RefsFont;
                                }

                                Color headColor = GetHeadColor(head);
                                Brush textBrush = new SolidBrush(headColor);

                                string headName;
                                PointF location;

                                if (IsCardLayout())
                                {
                                    headName = head.Name;
                                    offset += e.Graphics.MeasureString(headName, refsFont).Width + 6;
                                    location = new PointF(e.CellBounds.Right - offset, e.CellBounds.Top + 4);
                                    var size = new SizeF(e.Graphics.MeasureString(headName, refsFont).Width,
                                                         e.Graphics.MeasureString(headName, RefsFont).Height);
                                    e.Graphics.FillRectangle(new SolidBrush(SystemColors.Info), location.X - 1,
                                                             location.Y - 1, size.Width + 3, size.Height + 2);
                                    e.Graphics.DrawRectangle(new Pen(SystemColors.InfoText), location.X - 1,
                                                             location.Y - 1, size.Width + 3, size.Height + 2);
                                    e.Graphics.DrawString(headName, refsFont, textBrush, location);
                                }
                                else
                                {
                                    headName = IsFilledBranchesLayout()
                                                   ? head.Name
                                                   : string.Concat("[", head.Name, "] ");

                                    var headBounds = AdjustCellBounds(e.CellBounds, offset);
                                    SizeF textSize = e.Graphics.MeasureString(headName, refsFont);

                                    offset += textSize.Width;

                                    if (IsFilledBranchesLayout())
                                    {
                                        offset += 9;

                                        float extraOffset = DrawHeadBackground(isRowSelected, e.Graphics,
                                                                               headColor, headBounds.X,
                                                                               headBounds.Y,
                                                                               RoundToEven(textSize.Width + 3),
                                                                               RoundToEven(textSize.Height), 3,
                                                                               head.Selected,
                                                                               head.SelectedHeadMergeSource);

                                        offset += extraOffset;
                                        headBounds.Offset((int)(extraOffset + 1), 0);
                                    }

                                    DrawColumnText(e.Graphics, headName, refsFont, headColor, headBounds);
                                }
                            }
                        }

                        if (IsCardLayout())
                            offset = baseOffset;

                        var text = (string)e.FormattedValue;
                        var bounds = AdjustCellBounds(e.CellBounds, offset);
                        DrawColumnText(e.Graphics, text, rowFont, foreColor, bounds);

                        if (IsCardLayout())
                        {
                            int textHeight = (int)e.Graphics.MeasureString(text, rowFont).Height;
                            int gravatarSize = rowHeigth - textHeight - 12;
                            int gravatarTop = e.CellBounds.Top + textHeight + 6;
                            int gravatarLeft = e.CellBounds.Left + baseOffset + 2;


                            Image gravatar = Gravatar.GravatarService.GetImageFromCache(revision.AuthorEmail + gravatarSize.ToString() + ".png", revision.AuthorEmail, Settings.AuthorImageCacheDays, gravatarSize, Settings.GravatarCachePath, FallBackService.MonsterId);

                            if (gravatar == null && !string.IsNullOrEmpty(revision.AuthorEmail))
                            {
                                ThreadPool.QueueUserWorkItem(o =>
                                        Gravatar.GravatarService.LoadCachedImage(revision.AuthorEmail + gravatarSize.ToString() + ".png", revision.AuthorEmail, null, Settings.AuthorImageCacheDays, gravatarSize, Settings.GravatarCachePath, RefreshGravatar, FallBackService.MonsterId));
                            }

                            if (gravatar != null)
                                e.Graphics.DrawImage(gravatar, gravatarLeft + 1, gravatarTop + 1, gravatarSize, gravatarSize);

                            e.Graphics.DrawRectangle(Pens.Black, gravatarLeft, gravatarTop, gravatarSize + 1, gravatarSize + 1);

                            string authorText;
                            string timeText;

                            if (rowHeigth >= 60)
                            {
                                authorText = revision.Author;
                                timeText = TimeToString(Settings.ShowAuthorDate ? revision.AuthorDate : revision.CommitDate);
                            }
                            else
                            {
                                timeText = string.Concat(revision.Author, " (", TimeToString(Settings.ShowAuthorDate ? revision.AuthorDate : revision.CommitDate), ")");
                                authorText = string.Empty;
                            }



                            e.Graphics.DrawString(authorText, rowFont, foreBrush,
                                                  new PointF(gravatarLeft + gravatarSize + 5, gravatarTop + 6));
                            e.Graphics.DrawString(timeText, rowFont, foreBrush,
                                                  new PointF(gravatarLeft + gravatarSize + 5, e.CellBounds.Bottom - textHeight - 4));
                        }
                    }
                    break;
                case 2:
                    {
                        var text = (string)e.FormattedValue;
                        e.Graphics.DrawString(text, rowFont, foreBrush,
                                              new PointF(e.CellBounds.Left, e.CellBounds.Top + 4));
                    }
                    break;
                case 3:
                    {
                        var time = Settings.ShowAuthorDate ? revision.AuthorDate : revision.CommitDate;
                        var text = TimeToString(time);
                        e.Graphics.DrawString(text, rowFont, foreBrush,
                                              new PointF(e.CellBounds.Left, e.CellBounds.Top + 4));
                    }
                    break;
            }
        }

        private void RevisionsCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            var column = e.ColumnIndex;
            if (e.RowIndex < 0)
                return;

            if (Revisions.RowCount <= e.RowIndex)
                return;

            var revision = GetRevision(e.RowIndex);
            if (revision == null)
                return;

            e.FormattingApplied = true;

            switch (column)
            {
                case 0:
                    e.Value = revision.Guid;
                    break;
                case 1:
                    e.Value = revision.Message;
                    break;
                case 2:
                    e.Value = revision.Author ?? "";
                    break;
                case 3:
                    {
                        var time = Settings.ShowAuthorDate ? revision.AuthorDate : revision.CommitDate;
                        if (time == DateTime.MinValue || time == DateTime.MaxValue)
                            e.Value = "";
                        else
                            e.Value = string.Format("{0} {1}", time.ToShortDateString(), time.ToLongTimeString());
                    }
                    break;
            }
        }

        private void DrawColumnText(IDeviceContext dc, string text, Font font, Color color, Rectangle bounds)
        {
            TextRenderer.DrawText(dc, text, font, bounds, color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static Rectangle AdjustCellBounds(Rectangle cellBounds, float offset)
        {
            return new Rectangle((int)(cellBounds.Left + offset), cellBounds.Top + 4,
                                 cellBounds.Width - (int)offset, cellBounds.Height);
        }

        private static Color GetHeadColor(GitHead head)
        {
            return head.IsTag
                       ? Settings.TagColor
                       : head.IsHead
                             ? Settings.BranchColor
                             : head.IsRemote
                                   ? Settings.RemoteBranchColor
                                   : Settings.OtherTagColor;
        }

        private float RoundToEven(float value)
        {
            int result = ((int)value / 2) * 2;
            return result < value ? result + 2 : result;
        }

        private float DrawHeadBackground(bool isSelected, Graphics graphics, Color color,
            float x, float y, float width, float height, float radius, bool isCurrentBranch,
            bool isCurentBranchMergeSource)
        {
            float additionalOffset = isCurrentBranch || isCurentBranchMergeSource ? GetArrowSize(height) : 0;
            width += additionalOffset;
            var oldMode = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            try
            {
                // shade
                using (var shadePath = CreateRoundRectPath(x + 1, y + 1, width, height, radius))
                {
                    Color shadeColor = isSelected ? Color.Black : Color.Gray;
                    graphics.FillPath(new SolidBrush(shadeColor), shadePath);
                }

                using (var forePath = CreateRoundRectPath(x, y, width, height, radius))
                {
                    Color fillColor = Lerp(color, Color.White, 0.92F);

                    var fillBrush = new LinearGradientBrush(new RectangleF(x, y, width, height), fillColor,
                                                            Lerp(fillColor, Color.White, 0.9F), 90);

                    // fore rectangle
                    graphics.FillPath(fillBrush, forePath);
                    // frame
                    graphics.DrawPath(new Pen(Lerp(color, Color.White, 0.83F)), forePath);

                    // arrow if the head is the current branch 
                    if (isCurrentBranch)
                        DrawArrow(graphics, x, y, height, color, true);
                    else if (isCurentBranchMergeSource)
                        DrawArrow(graphics, x, y, height, color, false);
                }
            }
            finally
            {
                graphics.SmoothingMode = oldMode;
            }

            return additionalOffset;
        }

        private float GetArrowSize(float rowHeight)
        {
            return rowHeight - 6;
        }

        private void DrawArrow(Graphics graphics, float x, float y, float rowHeight, Color color, bool filled)
        {
            const float horShift = 4;
            const float verShift = 3;
            float height = rowHeight - verShift * 2;
            float width = height / 2;

            var points = new[]
                                 {
                                     new PointF(x + horShift, y + verShift),
                                     new PointF(x + horShift + width, y + verShift + height/2),
                                     new PointF(x + horShift, y + verShift + height),
                                     new PointF(x + horShift, y + verShift)
                                 };

            if (filled)
                graphics.FillPolygon(new SolidBrush(color), points);
            else
                graphics.DrawPolygon(new Pen(color), points);
        }

        private static GraphicsPath CreateRoundRectPath(float x, float y, float width, float height, float radius)
        {
            var path = new GraphicsPath();
            path.AddLine(x + radius, y, x + width - (radius * 2), y);
            path.AddArc(x + width - (radius * 2), y, radius * 2, radius * 2, 270, 90);
            path.AddLine(x + width, y + radius, x + width, y + height - (radius * 2));
            path.AddArc(x + width - (radius * 2), y + height - (radius * 2), radius * 2, radius * 2, 0, 90);
            path.AddLine(x + width - (radius * 2), y + height, x + radius, y + height);
            path.AddArc(x, y + height - (radius * 2), radius * 2, radius * 2, 90, 90);
            path.AddLine(x, y + height - (radius * 2), x, y + radius);
            path.AddArc(x, y, radius * 2, radius * 2, 180, 90);
            path.CloseFigure();
            return path;
        }

        private static float Lerp(float start, float end, float amount)
        {
            float difference = end - start;
            float adjusted = difference * amount;
            return start + adjusted;
        }

        private static Color Lerp(Color colour, Color to, float amount)
        {
            // start colours as lerp-able floats
            float sr = colour.R, sg = colour.G, sb = colour.B;

            // end colours as lerp-able floats
            float er = to.R, eg = to.G, eb = to.B;

            // lerp the colours to get the difference
            byte r = (byte)Lerp(sr, er, amount),
                 g = (byte)Lerp(sg, eg, amount),
                 b = (byte)Lerp(sb, eb, amount);

            // return the new colour
            return Color.FromArgb(r, g, b);
        }

        private void RefreshGravatar(Image image)
        {
            _syncContext.Post(state => Revisions.Refresh(), null);
        }


        private void RevisionsDoubleClick(object sender, EventArgs e)
        {
            ViewSelectedRevisions();
        }

        public void ViewSelectedRevisions()
        {
            var selectedRevisions = GetSelectedRevisions();
            if (selectedRevisions.Count > 0)
            {
                var form = new FormDiffSmall(selectedRevisions[0]);
                form.ShowDialog(this);
            }
            else
                GitUICommands.Instance.StartCompareRevisionsDialog(this);
        }

        private void SelectionTimerTick(object sender, EventArgs e)
        {
            SelectionTimer.Enabled = false;
            SelectionTimer.Stop();
            if (SelectionChanged != null)
                SelectionChanged(this, e);
        }

        private void CreateTagToolStripMenuItemClick(object sender, EventArgs e)
        {
            if (Revisions.RowCount <= LastRow || LastRow < 0)
                return;

            using (var frm = new FormTagSmall(GetRevision(LastRow)))
            {
                frm.ShowDialog(this);    
            }
            
            RefreshRevisions();
        }

        private void ResetCurrentBranchToHereToolStripMenuItemClick(object sender, EventArgs e)
        {
            if (Revisions.RowCount <= LastRow || LastRow < 0)
                return;

            var frm = new FormResetCurrentBranch(GetRevision(LastRow));
            frm.ShowDialog(this);
            RefreshRevisions();
            OnActionOnRepositoryPerformed();
        }

        private void CreateNewBranchToolStripMenuItemClick(object sender, EventArgs e)
        {
            if (Revisions.RowCount <= LastRow || LastRow < 0)
                return;
            var frm = new FormBranchSmall { Revision = GetRevision(LastRow) };

            if (frm.ShowDialog(this) == DialogResult.OK)
            {
                RefreshRevisions();
                OnActionOnRepositoryPerformed();
            }
        }

        private void RevisionsMouseClick(object sender, MouseEventArgs e)
        {
            var pt = Revisions.PointToClient(Cursor.Position);
            var hti = Revisions.HitTest(pt.X, pt.Y);
            LastRow = hti.RowIndex;
        }

        private void RevisionsCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
                return;

            var pt = Revisions.PointToClient(Cursor.Position);
            var hti = Revisions.HitTest(pt.X, pt.Y);

            if (LastRow == hti.RowIndex)
                return;

            LastRow = hti.RowIndex;
            Revisions.ClearSelection();

            if (LastRow >= 0 && Revisions.Rows.Count > LastRow)
                Revisions.Rows[LastRow].Selected = true;
        }

        private void CommitClick(object sender, EventArgs e)
        {
            GitUICommands.Instance.StartCommitDialog(this);
            OnActionOnRepositoryPerformed();
            RefreshRevisions();
        }

        private void GitIgnoreClick(object sender, EventArgs e)
        {
            GitUICommands.Instance.StartEditGitIgnoreDialog(this);
        }

        private void ShowRemoteBranchesClick(object sender, EventArgs e)
        {
            ShowRemoteBranches.Checked = !ShowRemoteBranches.Checked;
            Revisions.Invalidate();
        }

        private void ShowCurrentBranchOnlyToolStripMenuItemClick(object sender, EventArgs e)
        {
            if (showCurrentBranchOnlyToolStripMenuItem.Checked)
                return;

            Settings.BranchFilterEnabled = true;
            Settings.ShowCurrentBranchOnly = true;

            SetShowBranches();
            ForceRefreshRevisions();
        }

        private void ShowAllBranchesToolStripMenuItemClick(object sender, EventArgs e)
        {
            if (showAllBranchesToolStripMenuItem.Checked)
                return;

            Settings.BranchFilterEnabled = false;

            SetShowBranches();
            ForceRefreshRevisions();
        }

        private void ShowFilteredBranchesToolStripMenuItemClick(object sender, EventArgs e)
        {
            if (showFilteredBranchesToolStripMenuItem.Checked)
                return;

            Settings.BranchFilterEnabled = true;
            Settings.ShowCurrentBranchOnly = false;

            SetShowBranches();
            ForceRefreshRevisions();
        }

        private void SetShowBranches()
        {
            showAllBranchesToolStripMenuItem.Checked = !Settings.BranchFilterEnabled;
            showCurrentBranchOnlyToolStripMenuItem.Checked =
                Settings.BranchFilterEnabled && Settings.ShowCurrentBranchOnly;
            showFilteredBranchesToolStripMenuItem.Checked =
                Settings.BranchFilterEnabled && !Settings.ShowCurrentBranchOnly;

            BranchFilter = _revisionFilter.GetBranchFilter();

            if (!Settings.BranchFilterEnabled)
                LogParam = "HEAD --all --boundary";
            else if (Settings.ShowCurrentBranchOnly)
                LogParam = "HEAD";
            else
                LogParam = BranchFilter.Length > 0
                               ? String.Empty
                               : "HEAD --all --boundary";
        }

        private void RevertCommitToolStripMenuItemClick(object sender, EventArgs e)
        {
            if (Revisions.RowCount <= LastRow || LastRow < 0)
                return;

            var frm = new FormRevertCommitSmall(GetRevision(LastRow));
            frm.ShowDialog(this);
            RefreshRevisions();
        }

        private void FilterToolStripMenuItemClick(object sender, EventArgs e)
        {
            _revisionFilter.ShowDialog(this);
            ForceRefreshRevisions();
        }

        private void ApplyFilterFromRevisionFilterDialog()
        {
            BranchFilter = _revisionFilter.GetBranchFilter();
            SetShowBranches();
        }

        private void CreateTagOpening(object sender, CancelEventArgs e)
        {
            if (Revisions.RowCount < LastRow || LastRow < 0 || Revisions.RowCount == 0)
                return;

            var inTheMiddleOfBisect = Settings.Module.InTheMiddleOfBisect();
            markRevisionAsBadToolStripMenuItem.Visible = inTheMiddleOfBisect;
            markRevisionAsGoodToolStripMenuItem.Visible = inTheMiddleOfBisect;
            bisectSkipRevisionToolStripMenuItem.Visible = inTheMiddleOfBisect;
            stopBisectToolStripMenuItem.Visible = inTheMiddleOfBisect;
            bisectSeparator.Visible = inTheMiddleOfBisect;

            var revision = GetRevision(LastRow);

            var tagDropDown = new ContextMenuStrip();
            var deleteBranchDropDown = new ContextMenuStrip();
            var checkoutBranchDropDown = new ContextMenuStrip();
            var mergeBranchDropDown = new ContextMenuStrip();
            var rebaseDropDown = new ContextMenuStrip();
            var renameDropDown = new ContextMenuStrip();

            var tagNameCopy = new ContextMenuStrip();
            var branchNameCopy = new ContextMenuStrip();

            foreach (var head in revision.Heads.Where(h => h.IsTag))
            {
                ToolStripItem toolStripItem = new ToolStripMenuItem(head.Name);
                ToolStripItem tagName = new ToolStripMenuItem(head.Name);
                toolStripItem.Click += ToolStripItemClick;
                tagDropDown.Items.Add(toolStripItem);
                tagName.Click += copyToClipBoard;
                tagNameCopy.Items.Add(tagName);
            }

            //For now there is no action that could be done on currentBranch
            string currentBranch = Settings.Module.GetSelectedBranch();
            var allBranches = revision.Heads.Where(h => !h.IsTag && (h.IsHead || h.IsRemote));
            var localBranches = allBranches.Where(b => !b.IsRemote);

            var branchesWithNoIdenticalRemotes = allBranches.Where(
                b => !b.IsRemote || !localBranches.Any(lb => lb.TrackingRemote == b.Remote && lb.MergeWith == b.LocalName));

            bool currentBranchPointsToRevision = false;
            foreach (var head in branchesWithNoIdenticalRemotes)
            {
                if (head.Name.Equals(currentBranch))
                    currentBranchPointsToRevision = true;
                else
                {
                    ToolStripItem toolStripItem = new ToolStripMenuItem(head.Name);
                    toolStripItem.Click += ToolStripItemClickMergeBranch;
                    mergeBranchDropDown.Items.Add(toolStripItem);

                    toolStripItem = new ToolStripMenuItem(head.Name);
                    toolStripItem.Click += ToolStripItemClickRebaseBranch;
                    rebaseDropDown.Items.Add(toolStripItem);
                }
            }

            //if there is no branch to rebase on, then allow user to rebase on selected commit 
            if (rebaseDropDown.Items.Count == 0 && !currentBranchPointsToRevision)
            {
                ToolStripItem toolStripItem = new ToolStripMenuItem(revision.Guid);
                toolStripItem.Click += ToolStripItemClickRebaseBranch;
                rebaseDropDown.Items.Add(toolStripItem);
            }

            //if there is no branch to merge, then let user to merge selected commit into current branch 
            if (mergeBranchDropDown.Items.Count == 0 && !currentBranchPointsToRevision)
            {
                ToolStripItem toolStripItem = new ToolStripMenuItem(revision.Guid);
                toolStripItem.Click += ToolStripItemClickMergeBranch;
                mergeBranchDropDown.Items.Add(toolStripItem);
            }


            foreach (var head in allBranches)
            {
                ToolStripItem toolStripItem = new ToolStripMenuItem(head.Name);
                ToolStripItem branchName = new ToolStripMenuItem(head.Name);
                branchName.Click += copyToClipBoard;
                branchNameCopy.Items.Add(branchName);

                //skip remote branches - they can not be deleted this way
                if (!head.IsRemote)
                {
                    if (!head.Name.Equals(currentBranch))
                    {
                        toolStripItem = new ToolStripMenuItem(head.Name);
                        toolStripItem.Click += ToolStripItemClickBranch;
                        deleteBranchDropDown.Items.Add(toolStripItem); //Add to delete branch
                    }

                    toolStripItem = new ToolStripMenuItem(head.Name);
                    toolStripItem.Click += ToolStripItemClickRenameBranch;
                    renameDropDown.Items.Add(toolStripItem); //Add to rename branch
                }

                if (!head.Name.Equals(currentBranch))
                {

                    toolStripItem = new ToolStripMenuItem(head.Name);
                    if (head.IsRemote)
                        toolStripItem.Click += ToolStripItemClickCheckoutRemoteBranch;
                    else
                        toolStripItem.Click += ToolStripItemClickCheckoutBranch;
                    checkoutBranchDropDown.Items.Add(toolStripItem);
                }
            }

            deleteTagToolStripMenuItem.DropDown = tagDropDown;
            deleteTagToolStripMenuItem.Visible = tagDropDown.Items.Count > 0;

            deleteBranchToolStripMenuItem.DropDown = deleteBranchDropDown;
            deleteBranchToolStripMenuItem.Visible = deleteBranchDropDown.Items.Count > 0;

            checkoutBranchToolStripMenuItem.DropDown = checkoutBranchDropDown;
            checkoutBranchToolStripMenuItem.Visible = checkoutBranchDropDown.Items.Count > 0;

            mergeBranchToolStripMenuItem.DropDown = mergeBranchDropDown;
            mergeBranchToolStripMenuItem.Visible = mergeBranchDropDown.Items.Count > 0;

            rebaseOnToolStripMenuItem.DropDown = rebaseDropDown;
            rebaseOnToolStripMenuItem.Visible = rebaseDropDown.Items.Count > 0;

            renameBranchToolStripMenuItem.DropDown = renameDropDown;
            renameBranchToolStripMenuItem.Visible = renameDropDown.Items.Count > 0;

            branchNameToolStripMenuItem.DropDown = branchNameCopy;
            branchNameToolStripMenuItem.Visible = branchNameCopy.Items.Count > 0;

            tagToolStripMenuItem.DropDown = tagNameCopy;
            tagToolStripMenuItem.Visible = tagNameCopy.Items.Count > 0;

            toolStripSeparator6.Visible = tagNameCopy.Items.Count > 0 || branchNameCopy.Items.Count > 0;

            RefreshOwnScripts();

            goToParentToolStripMenuItem.Visible = revision.HasParent();
        }

        private void ToolStripItemClick(object sender, EventArgs e)
        {
            var toolStripItem = sender as ToolStripItem;

            if (toolStripItem == null)
                return;

            FormProcess.ShowDialog(this, GitCommandHelpers.DeleteTagCmd(toolStripItem.Text));
            ForceRefreshRevisions();
        }

        private void ToolStripItemClickBranch(object sender, EventArgs e)
        {
            var toolStripItem = sender as ToolStripItem;

            if (toolStripItem == null)
                return;

            GitUICommands.Instance.StartDeleteBranchDialog(this, toolStripItem.Text);

            ForceRefreshRevisions();
        }

        private void CheckoutBranch(string branch)
        {
            var command = GitCommandHelpers.CheckoutCmd(branch);
            FormProcess.ShowDialog(this, command);

            ForceRefreshRevisions();
            OnActionOnRepositoryPerformed();
        }

        private void ToolStripItemClickCheckoutBranch(object sender, EventArgs e)
        {
            var toolStripItem = sender as ToolStripItem;

            if (toolStripItem == null)
                return;

            CheckoutBranch(toolStripItem.Text);
            }

        private void ToolStripItemClickCheckoutRemoteBranch(object sender, EventArgs e)
        {
            var toolStripItem = sender as ToolStripItem;

            if (toolStripItem == null)
                return;

            GitUICommands.Instance.StartCheckoutRemoteBranchDialog(this, toolStripItem.Text);

            ForceRefreshRevisions();
            OnActionOnRepositoryPerformed();
        }

        private void ToolStripItemClickMergeBranch(object sender, EventArgs e)
        {
            var toolStripItem = sender as ToolStripItem;

            if (toolStripItem == null)
                return;

            GitUICommands.Instance.StartMergeBranchDialog(this, toolStripItem.Text);

            ForceRefreshRevisions();
            OnActionOnRepositoryPerformed();
        }

        private void ToolStripItemClickRebaseBranch(object sender, EventArgs e)
        {
            var toolStripItem = sender as ToolStripItem;

            if (toolStripItem == null)
                return;

            GitUICommands.Instance.StartRebaseDialog(this, toolStripItem.Text);

            ForceRefreshRevisions();
            OnActionOnRepositoryPerformed();
        }

        private void ToolStripItemClickRenameBranch(object sender, EventArgs e)
        {
            var toolStripItem = sender as ToolStripItem;

            if (toolStripItem == null)
                return;

            var renameExecuted = GitUICommands.Instance.StartRenameDialog(this, toolStripItem.Text);
            if (!renameExecuted)
                return;

            ForceRefreshRevisions();
            OnActionOnRepositoryPerformed();
        }

        private void CheckoutRevisionToolStripMenuItemClick(object sender, EventArgs e)
        {
            if (Revisions.RowCount <= LastRow || LastRow < 0)
                return;

            if (MessageBox.Show(this, _areYouSureYouWantCheckout.Text, _areYouSureYouWantCheckoutCaption.Text,
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            CheckoutBranch(GetRevision(LastRow).Guid);
            }

        private void ShowAuthorDateToolStripMenuItemClick(object sender, EventArgs e)
        {
            Settings.ShowAuthorDate = !showAuthorDateToolStripMenuItem.Checked;
            showAuthorDateToolStripMenuItem.Checked = Settings.ShowAuthorDate;
            ForceRefreshRevisions();
        }

        private void OrderRevisionsByDateToolStripMenuItemClick(object sender, EventArgs e)
        {
            Settings.OrderRevisionByDate = !orderRevisionsByDateToolStripMenuItem.Checked;
            orderRevisionsByDateToolStripMenuItem.Checked = Settings.OrderRevisionByDate;
            ForceRefreshRevisions();
        }

        private void CherryPickCommitToolStripMenuItemClick(object sender, EventArgs e)
        {
            var revisions = GetSelectedRevisions(SortDirection.Descending);

            FormCherryPickCommitSmall prevForm = null;

            try
            {
                foreach (var r in revisions)
                {
                    var frm = new FormCherryPickCommitSmall(r);
                    if (prevForm != null)
                    {
                        frm.CopyOptions(prevForm);
                        prevForm.Dispose();
                    }
                    prevForm = frm;
                    if (frm.ShowDialog(this) != DialogResult.OK)
                        break;
                }
            }
            finally
            {
                if (prevForm != null)
                    prevForm.Dispose();
            }

            ForceRefreshRevisions();
            OnActionOnRepositoryPerformed();
        }

        private void FixupCommitToolStripMenuItemClick(object sender, EventArgs e)
        {
            PrepareCorrectionCommit(CommitKind.Fixup);
        }

        private void SquashCommitToolStripMenuItemClick(object sender, EventArgs e)
        {
            PrepareCorrectionCommit(CommitKind.Squash);
        }

        private void PrepareCorrectionCommit(CommitKind commitKind)
        {
            if (Revisions.RowCount <= LastRow || LastRow < 0)
                return;

            var frm = new FormCommit(commitKind, GetRevision(LastRow));
            frm.ShowDialog(this);
            ForceRefreshRevisions();
            OnActionOnRepositoryPerformed();
        }

        private void ShowRelativeDateToolStripMenuItemClick(object sender, EventArgs e)
        {
            Settings.RelativeDate = !showRelativeDateToolStripMenuItem.Checked;
            showRelativeDateToolStripMenuItem.Checked = Settings.RelativeDate;
            ForceRefreshRevisions();
        }

        private string TimeToString(DateTime time)
        {
            if (time == DateTime.MinValue || time == DateTime.MaxValue)
                return "";

            if (!Settings.RelativeDate)
                return string.Format("{0} {1}", time.ToShortDateString(), time.ToLongTimeString());

            return GitCommandHelpers.GetRelativeDateString(DateTime.Now, time, false);
        }

        private void UpdateGraph(GitRevision rev)
        {
            if (rev == null)
            {
                // Prune the graph and make sure the row count matches reality
                Revisions.Prune();

                if (Revisions.RowCount == 0 && Settings.RevisionGraphShowWorkingDirChanges)
                {
                    bool uncommittedChanges = false;
                    bool stagedChanges = false;
                    //Only check for tracked files. This usually makes more sense and it performs a lot
                    //better then checking for untracked files.
                    if (Settings.Module.GetTrackedChangedFiles().Count > 0)
                        uncommittedChanges = true;
                    if (Settings.Module.GetStagedFiles().Count > 0)
                        stagedChanges = true;

                    if (uncommittedChanges)
                    {
                        //Add working dir as virtual commit
                        var workingDir = new GitRevision(GitRevision.UncommittedWorkingDirGuid)
                                             {
                                                 Message = _currentWorkingDirChanges.Text,
                                                 ParentGuids =
                                                     stagedChanges
                                                         ? new[] { GitRevision.IndexGuid }
                                                         : new[] { CurrentCheckout }
                                             };
                        Revisions.Add(workingDir.Guid, workingDir.ParentGuids, DvcsGraph.DataType.Normal, workingDir);
                    }

                    if (stagedChanges)
                    {
                        //Add index as virtual commit
                        var index = new GitRevision(GitRevision.IndexGuid)
                                        {
                                            Message = _currentIndex.Text,
                                            ParentGuids = new string[] { CurrentCheckout }
                                        };
                        Revisions.Add(index.Guid, index.ParentGuids, DvcsGraph.DataType.Normal, index);
                    }
                }
                return;
            }

            var dataType = DvcsGraph.DataType.Normal;
            if (rev.Guid == CurrentCheckout)
                dataType = DvcsGraph.DataType.Active;
            else if (rev.Heads.Count > 0)
                dataType = DvcsGraph.DataType.Special;

            Revisions.Add(rev.Guid, rev.ParentGuids, dataType, rev);
        }

        private void drawNonrelativesGrayToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Settings.RevisionGraphDrawNonRelativesGray = !Settings.RevisionGraphDrawNonRelativesGray;
            drawNonrelativesGrayToolStripMenuItem.Checked = Settings.RevisionGraphDrawNonRelativesGray;
            Revisions.Refresh();
        }

        private void messageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Clipboard.SetText(GetRevision(LastRow).Message);
        }

        private void authorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Clipboard.SetText(GetRevision(LastRow).Author);
        }

        private void dateToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Clipboard.SetText(GetRevision(LastRow).CommitDate.ToString());
        }

        private void hashToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Clipboard.SetText(GetRevision(LastRow).Guid);
        }

        private static void copyToClipBoard(object sender, EventArgs e)
        {
            Clipboard.SetText(sender.ToString());
        }

        private void markRevisionAsBadToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ContinueBisect(GitBisectOption.Bad);
        }

        private void markRevisionAsGoodToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ContinueBisect(GitBisectOption.Good);
        }

        private void bisectSkipRevisionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ContinueBisect(GitBisectOption.Skip);
        }

        private void ContinueBisect(GitBisectOption bisectOption)
        {
            if (Revisions.RowCount <= LastRow || LastRow < 0)
                return;

            FormProcess.ShowDialog(this, GitCommandHelpers.ContinueBisectCmd(bisectOption, GetRevision(LastRow).Guid), false);
            RefreshRevisions();
        }

        private void stopBisectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FormProcess.ShowDialog(this, GitCommandHelpers.StopBisectCmd());
            RefreshRevisions();
        }

        private void RefreshOwnScripts()
        {
            RemoveOwnScripts();
            AddOwnScripts();
        }

        private void AddOwnScripts()
        {
            IList<ScriptInfo> scripts = ScriptManager.GetScripts();
            int addedScripts = 0;
            if (scripts != null)
            {
                foreach (ScriptInfo scriptInfo in scripts)
                {
                    if (scriptInfo.Enabled)
                    {
                        addedScripts++;
                        ToolStripItem item = new ToolStripMenuItem(scriptInfo.Name);
                        item.Name = item.Text + "_ownScript";
                        item.Click += runScript;
                        if (scriptInfo.AddToRevisionGridContextMenu)
                            CreateTag.Items.Add(item);
                        else
                            runScriptToolStripMenuItem.DropDown.Items.Add(item);
                    }
                }
                toolStripSeparator7.Visible = addedScripts > 1;
                runScriptToolStripMenuItem.Visible = runScriptToolStripMenuItem.DropDown.Items.Count > 0;
            }
        }

        private void RemoveOwnScripts()
        {
            runScriptToolStripMenuItem.DropDown.Items.Clear();
            List<ToolStripItem> list = new List<ToolStripItem>();
            foreach (ToolStripItem item in CreateTag.Items)
                list.Add(item);
            foreach (ToolStripItem item in list)
                if (item.Name.Contains("_ownScript"))
                    CreateTag.Items.RemoveByKey(item.Name);
        }

        private bool settingsLoaded;

        private void runScript(object sender, EventArgs e)
        {
            if (settingsLoaded == false)
            {
                new FormSettings().LoadSettings();
                settingsLoaded = true;
            }
            ScriptRunner.RunScript(sender.ToString(), this);
            RefreshRevisions();
        }

        #region Drag/drop patch files on revision grid

        void Revisions_DragDrop(object sender, DragEventArgs e)
        {
            var fileNameArray = e.Data.GetData(DataFormats.FileDrop) as Array;
            if (fileNameArray != null)
            {
                if (fileNameArray.Length > 10)
                {
                    //Some users need to be protected against themselves!
                    MessageBox.Show(this, _droppingFilesBlocked.Text);
                    return;
                }

                foreach (object fileNameObject in fileNameArray)
                {
                    var fileName = fileNameObject as string;

                    if (!string.IsNullOrEmpty(fileName) && fileName.EndsWith(".patch", StringComparison.InvariantCultureIgnoreCase))
                    {
                        //Start apply patch dialog for each dropped patch file...
                        GitUICommands.Instance.StartApplyPatchDialog(this, fileName);
                    }
                }
            }
        }

        static void Revisions_DragEnter(object sender, DragEventArgs e)
        {
            var fileNameArray = e.Data.GetData(DataFormats.FileDrop) as Array;
            if (fileNameArray != null)
            {
                foreach (object fileNameObject in fileNameArray)
                {
                    var fileName = fileNameObject as string;

                    if (!string.IsNullOrEmpty(fileName) && fileName.EndsWith(".patch", StringComparison.InvariantCultureIgnoreCase))
                    {
                        //Allow drop (copy, not move) patch files
                        e.Effect = DragDropEffects.Copy;
                    }
                    else
                    {
                        //When a non-patch file is dragged, do not allow it
                        e.Effect = DragDropEffects.None;
                        return;
                    }
                }
            }
        }
        #endregion

        private void ShowGitNotesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Settings.ShowGitNotes = !showGitNotesToolStripMenuItem.Checked;
            showGitNotesToolStripMenuItem.Checked = Settings.ShowGitNotes;

            ForceRefreshRevisions();
        }
        private void InitRepository_Click(object sender, EventArgs e)
        {
            if (GitUICommands.Instance.StartInitializeDialog(this, Settings.WorkingDir))
                ForceRefreshRevisions();
        }

        private void CloneRepository_Click(object sender, EventArgs e)
        {
            if (GitUICommands.Instance.StartCloneDialog(this))
                ForceRefreshRevisions();
        }

        private void ShowRevisionGraphToolStripMenuItemClick(object sender, EventArgs e)
        {
            if (Settings.RevisionGraphLayout == (int)RevisionGridLayout.Small) Settings.RevisionGraphLayout = (int)RevisionGridLayout.SmallWithGraph;
            else if (Settings.RevisionGraphLayout == (int)RevisionGridLayout.Card) Settings.RevisionGraphLayout = (int)RevisionGridLayout.CardWithGraph;
            else if (Settings.RevisionGraphLayout == (int)RevisionGridLayout.LargeCard) Settings.RevisionGraphLayout = (int)RevisionGridLayout.LargeCardWithGraph;
            else if (Settings.RevisionGraphLayout == (int)RevisionGridLayout.SmallWithGraph) Settings.RevisionGraphLayout = (int)RevisionGridLayout.Small;
            else if (Settings.RevisionGraphLayout == (int)RevisionGridLayout.CardWithGraph) Settings.RevisionGraphLayout = (int)RevisionGridLayout.Card;
            else if (Settings.RevisionGraphLayout == (int)RevisionGridLayout.LargeCardWithGraph) Settings.RevisionGraphLayout = (int)RevisionGridLayout.LargeCard;
            else if (Settings.RevisionGraphLayout == (int)RevisionGridLayout.FilledBranchesSmall) Settings.RevisionGraphLayout = (int)RevisionGridLayout.FilledBranchesSmallWithGraph;
            else if (Settings.RevisionGraphLayout == (int)RevisionGridLayout.FilledBranchesSmallWithGraph) Settings.RevisionGraphLayout = (int)RevisionGridLayout.FilledBranchesSmall;
            SetRevisionsLayout();
            Refresh();
        }

        public void ToggleRevisionCardLayout()
        {
            var layouts = new List<RevisionGridLayout>((RevisionGridLayout[])Enum.GetValues(typeof(RevisionGridLayout)));
            layouts.Sort();
            var maxLayout = (int)layouts[layouts.Count - 1];

            int nextLayout = Settings.RevisionGraphLayout + 1;

            if (nextLayout > maxLayout)
                nextLayout = 1;

            SetRevisionsLayout((RevisionGridLayout)nextLayout);
        }

        public void SetRevisionsLayout(RevisionGridLayout revisionGridLayout)
        {
            Settings.RevisionGraphLayout = (int)revisionGridLayout;
            SetRevisionsLayout();
        }

        private void SetRevisionsLayout()
        {
            layout = Enum.IsDefined(typeof(RevisionGridLayout), Settings.RevisionGraphLayout)
                         ? (RevisionGridLayout)Settings.RevisionGraphLayout
                         : RevisionGridLayout.SmallWithGraph;

            showRevisionGraphToolStripMenuItem.Checked = IsGraphLayout();
            IsCardLayout();

            if (IsFilledBranchesLayout())
            {
                NormalFont = new Font("Tahoma", 8.75F); // SystemFonts.DefaultFont.FontFamily, SystemFonts.DefaultFont.Size + 2);
            }
            else
            {
                NormalFont = new Font("Tahoma", 8.75F);
            }

            if (IsCardLayout())
            {
                if (Settings.RevisionGraphLayout == (int)RevisionGridLayout.Card
                    || Settings.RevisionGraphLayout == (int)RevisionGridLayout.CardWithGraph)
                {
                    rowHeigth = 45;
                }
                else
                {
                    rowHeigth = 70;
                }

                selectedItemBrush = new LinearGradientBrush(new Rectangle(0, 0, rowHeigth, rowHeigth),
                Revisions.RowTemplate.DefaultCellStyle.SelectionBackColor,
                Color.LightBlue, 90, false);

                Revisions.ShowAuthor(!IsCardLayout());
                Revisions.SetDimensions(NODE_DIMENSION, LANE_WIDTH, LANE_LINE_WIDTH, rowHeigth, selectedItemBrush);

            }
            else
            {
                if (IsFilledBranchesLayout())
                {
                    using (var graphics = Graphics.FromHwnd(Handle))
                    {
                        rowHeigth = (int)graphics.MeasureString("By", NormalFont).Height + 9;
                    }

                    selectedItemBrush = SystemBrushes.Highlight;
                }
                else
                {
                    rowHeigth = 25;

                    selectedItemBrush = new LinearGradientBrush(new Rectangle(0, 0, rowHeigth, rowHeigth),
                                                                Revisions.RowTemplate.DefaultCellStyle.SelectionBackColor,
                                                                Color.LightBlue, 90, false);
                }

                Revisions.ShowAuthor(!IsCardLayout());
                Revisions.SetDimensions(NODE_DIMENSION, LANE_WIDTH, LANE_LINE_WIDTH, rowHeigth, selectedItemBrush);
            }

            //Hide graph column when there it is disabled OR when a filter is active
            //allowing for special case when history of a single file is being displayed
            if (!IsGraphLayout() || (ShouldHideGraph(false) && !AllowGraphWithFilter))
            {
                Revisions.HideRevisionGraph();
            }
            else
            {
                Revisions.ShowRevisionGraph();
            }
        }

        private bool IsFilledBranchesLayout()
        {
            return layout == RevisionGridLayout.FilledBranchesSmall || layout == RevisionGridLayout.FilledBranchesSmallWithGraph;
        }

        private bool IsCardLayout()
        {
            return layout == RevisionGridLayout.Card
                   || layout == RevisionGridLayout.CardWithGraph
                   || layout == RevisionGridLayout.LargeCard
                   || layout == RevisionGridLayout.LargeCardWithGraph;
        }

        private bool IsGraphLayout()
        {
            return layout == RevisionGridLayout.SmallWithGraph
                   || layout == RevisionGridLayout.CardWithGraph
                   || layout == RevisionGridLayout.LargeCardWithGraph
                   || layout == RevisionGridLayout.FilledBranchesSmallWithGraph;
        }

        #region Hotkey commands

        public const string HotkeySettingsName = "RevisionGrid";

        internal enum Commands
        {
            ToggleRevisionGraph,
            RevisionFilter,
            ToggleAuthorDateCommitDate,
            ToggleOrderRevisionsByDate,
            ToggleShowRelativeDate,
            ToggleDrawNonRelativesGray,
            ToggleShowGitNotes,
            ToggleRevisionCardLayout,
            ShowAllBranches,
            ShowCurrentBranchOnly
        }

        protected override bool ExecuteCommand(int cmd)
        {
            Commands command = (Commands)cmd;

            switch (command)
            {
                case Commands.ToggleRevisionGraph: ShowRevisionGraphToolStripMenuItemClick(null, null); break;
                case Commands.RevisionFilter: FilterToolStripMenuItemClick(null, null); break;
                case Commands.ToggleAuthorDateCommitDate: ShowAuthorDateToolStripMenuItemClick(null, null); break;
                case Commands.ToggleOrderRevisionsByDate: OrderRevisionsByDateToolStripMenuItemClick(null, null); break;
                case Commands.ToggleShowRelativeDate: ShowRelativeDateToolStripMenuItemClick(null, null); break;
                case Commands.ToggleDrawNonRelativesGray: drawNonrelativesGrayToolStripMenuItem_Click(null, null); break;
                case Commands.ToggleShowGitNotes: ShowGitNotesToolStripMenuItem_Click(null, null); break;
                case Commands.ToggleRevisionCardLayout: ToggleRevisionCardLayout(); break;
                case Commands.ShowAllBranches: ShowAllBranchesToolStripMenuItemClick(null, null); break;
                case Commands.ShowCurrentBranchOnly: ShowCurrentBranchOnlyToolStripMenuItemClick(null, null); break;
                default: ExecuteScriptCommand(cmd, Keys.None); break;
            }

            return true;
        }

        #endregion

        private void toolStripMenuWithOneItem_Click(object sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem)
            {
                ToolStripMenuItem item = sender as ToolStripMenuItem;
                if (item.DropDown != null && item.DropDown.Items.Count == 1)
                    item.DropDown.Items[0].PerformClick();
            }
        }

        private void goToParentToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var r = GetRevision(LastRow);
            if (r.HasParent())
                SetSelectedRevision(new GitRevision(r.ParentGuids[0]));
        }
    }
}