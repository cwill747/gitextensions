﻿using System.Collections.Generic;

namespace GitCommands
{
    public class GitItem : IGitItem
    {
        internal const int MinimumStringLength = 53;

        public string Guid { get; set; }
        public string CommitGuid { get; set; }
        public string ItemType{ get; set; }
        public string Name { get; set; }
        public string Author { get; set; }
        public string Date { get; set; }
        public string FileName { get; set; }
        public string Mode { get; set; }

        private List<IGitItem> subItems;

        public bool IsBlob
        {
            get { return ItemType == "blob"; }
        }

        public bool IsCommit
        {
            get { return ItemType == "commit"; }
        }

        public bool IsTree
        {
            get { return ItemType == "tree"; }
        }

        public List<IGitItem> SubItems
        {
            get
            {
                if (subItems == null)
                {
                    subItems = Settings.Module.GetTree(Guid, false);

                    foreach (GitItem item in subItems)
                    {
                        item.FileName = FileName + Settings.PathSeparator.ToString() + item.FileName;
                    }
                }

                return subItems;
            }
        }

        internal static GitItem CreateGitItemFromString(string itemsString)
        {
            if ((itemsString == null) || (itemsString.Length <= MinimumStringLength))
                return null;

            var guidStart = itemsString.IndexOf(' ', 7);

            var item = new GitItem
                           {
                               Mode = itemsString.Substring(0, 6),
                               ItemType = itemsString.Substring(7, guidStart - 7),
                               Guid = itemsString.Substring(guidStart + 1, 40),
                               Name = itemsString.Substring(guidStart + 42).Trim()
                           };

            item.FileName = item.Name;
            return item;
        }


        public static List<GitItem> CreateGitItemsFromString(string tree)
        {
            var itemsStrings = tree.Split(new char[] { '\0', '\n' });

            var items = new List<GitItem>();

            foreach (var itemsString in itemsStrings)
            {
                if (itemsString.Length <= 53)
                    continue;

                var item = GitItem.CreateGitItemFromString(itemsString);

                items.Add(item);
            }

            return items;
        }

        public static List<IGitItem> CreateIGitItemsFromString(string tree)
        {
            var items = new List<IGitItem>();

            foreach (var item in CreateGitItemsFromString(tree))
                items.Add(item);

            return items;
        }


    }
}
