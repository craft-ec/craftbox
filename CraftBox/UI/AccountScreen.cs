using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using CraftBox.Core;

namespace CraftBox.UI
{
    class AccountScreen : Screen
    {
        readonly List<Account> accounts;
        int selected;
        readonly List<View> avatars = new List<View>();

        public AccountScreen(App app) : base(app)
        {
            accounts = app.Accounts.Accounts;

            Add(Theme.Label("Who's watching?", 52, Theme.Text, 0, 120, Theme.W, 70));
            var title = (TextLabel)GetChildAt(GetChildCount() - 1);
            title.HorizontalAlignment = HorizontalAlignment.Center;

            float avatarSize = 140;
            float gap = 40;
            float totalW = accounts.Count * avatarSize + (accounts.Count - 1) * gap;
            float startX = (Theme.W - totalW) / 2f;
            float startY = (Theme.H - avatarSize - 40) / 2f;

            for (int i = 0; i < accounts.Count; i++)
            {
                var a = accounts[i];
                var color = new Color(a.ColorR, a.ColorG, a.ColorB, 1f);

                var container = new View
                {
                    Position = new Position(startX + i * (avatarSize + gap), startY),
                    Size = new Size(avatarSize, avatarSize + 60),
                };

                var ring = new View
                {
                    Name = "ring",
                    Position = new Position(-4, -4),
                    Size = new Size(avatarSize + 8, avatarSize + 8),
                    BackgroundColor = Theme.Text,
                };
                ring.Hide();
                container.Add(ring);

                var circle = new View
                {
                    Size = new Size(avatarSize, avatarSize),
                    BackgroundColor = color,
                };
                container.Add(circle);

                var initial = Theme.Label(a.Name.Substring(0, 1).ToUpper(), 56, Theme.Text,
                    0, 0, avatarSize, avatarSize);
                initial.HorizontalAlignment = HorizontalAlignment.Center;
                initial.VerticalAlignment = VerticalAlignment.Center;
                container.Add(initial);

                var nameLabel = Theme.Label(a.Name, 24, Theme.Muted, 0, avatarSize + 12, avatarSize, 30);
                nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
                nameLabel.Name = "name";
                container.Add(nameLabel);

                if (a.Age > 0)
                {
                    var ageLabel = Theme.Label($"Age {a.Age}", 18, Theme.Muted, 0, avatarSize + 38, avatarSize, 24);
                    ageLabel.HorizontalAlignment = HorizontalAlignment.Center;
                    container.Add(ageLabel);
                }

                Add(container);
                avatars.Add(container);
            }

            var hint = Theme.Label("Press OK to select profile", 22, Theme.Muted, 0, Theme.H - 100, Theme.W, 30);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            Add(hint);

            Refresh();
        }

        void Refresh()
        {
            for (int i = 0; i < avatars.Count; i++)
            {
                var container = avatars[i];
                bool focused = i == selected;
                container.Scale = focused ? new Vector3(1.1f, 1.1f, 1f) : Vector3.One;
                var ring = container.FindChildByName("ring");
                if (ring != null) { if (focused) ring.Show(); else ring.Hide(); }
                var name = container.FindChildByName("name") as TextLabel;
                if (name != null) name.TextColor = focused ? Theme.Text : Theme.Muted;
            }
        }

        public override bool OnKey(string key)
        {
            switch (key)
            {
                case "Left":
                    if (selected > 0) selected--;
                    Refresh();
                    return true;
                case "Right":
                    if (selected < accounts.Count - 1) selected++;
                    Refresh();
                    return true;
                case "Return": case "KP_Enter": case "Select":
                    App.SelectAccount(accounts[selected]);
                    return true;
            }
            return false;
        }
    }
}
