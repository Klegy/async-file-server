﻿namespace AaronLuna.AsyncFileServer.Console.Menus
{
    using System;
    using System.Threading.Tasks;

    using Common.Console.Menu;
    using Common.Logging;
    using Common.Result;

    using EventLogsMenus;
    using EventLogsMenus.EventLogsMenuItems;
    using MainMenuItems;
    using PendingRequestsMenus;
    using PendingRequestsMenus.PendingRequestsMenuItems;
    using RemoteServerMenus;
    using RemoteServerMenus.RemoteServerMenuItems;
    using ServerConfigurationMenus;

    class MainMenu : ITieredMenu
    {
        readonly AppState _state;
        readonly ShutdownServerMenuItem _shutdownServer;
        readonly Logger _log = new Logger(typeof(MainMenu));

        public MainMenu(AppState state)
        {
            _state = state;
            _shutdownServer = new ShutdownServerMenuItem(state);

            ReturnToParent = true;
            ItemText = "Main menu";
            MenuText = "Main Menu:";
            TieredMenu = new TieredMenu();
        }

        public string ItemText { get; set; }
        public bool ReturnToParent { get; set; }
        public string MenuText { get; set; }
        public TieredMenu TieredMenu { get; set; }

        public Result DisplayMenu()
        {
            if (_state.DoNotRefreshMainMenu) return Result.Ok();
            SharedFunctions.DisplayLocalServerInfo(_state);

            PopulateMenu();
            ConsoleMenu.DisplayTieredMenu(TieredMenu);
            Console.WriteLine(ValidRangeOfMenuItemNumbers());

            return Result.Ok();
        }

        public async Task<Result> ExecuteAsync()
        {
            var exit = false;
            Result result = null;

            while (!exit)
            {
                _state.DoNotRefreshMainMenu = false;

                if (_state.RestartRequired)
                {
                    exit = true;
                    continue;
                }

                SharedFunctions.DisplayLocalServerInfo(_state);
                PopulateMenu();

                var menuItem = await GetUserSelectionAsync().ConfigureAwait(false);
                result = await menuItem.ExecuteAsync().ConfigureAwait(false);
                exit = menuItem.ReturnToParent;

                if (result.Success) continue;

                _log.Error($"Error: {result.Error}");
                Console.WriteLine(Environment.NewLine + result.Error + Environment.NewLine);
                Console.WriteLine("Press enter to return to the main menu.");
                Console.ReadLine();
            }

            return result;
        }

        void PopulateMenu()
        {
            TieredMenu.Clear();

            PopulatePendingRequestsMenuTier();
            PopulateSelectRemoteServerMenuTier();            
            PopulateRemoteServerMenuTier();
            PopulateViewLogsMenuTier();
            PopulateServerConfigurationMenuTier();
        }

        async Task<IMenuItem> GetUserSelectionAsync()
        {
            var userSelection = 0;
            while (userSelection == 0)
            {
                ConsoleMenu.DisplayTieredMenu(TieredMenu);
                Console.WriteLine(ValidRangeOfMenuItemNumbers());
                var input = Console.ReadLine();

                var inputValidation =
                    SharedFunctions.ValidateNumberIsWithinRange(input, 1, TieredMenu.ItemCount);

                if (inputValidation.Failure)
                {
                    Console.WriteLine(Environment.NewLine + inputValidation.Error);
                    await Task.Delay(_state.MessageDisplayTime).ConfigureAwait(false);

                    SharedFunctions.DisplayLocalServerInfo(_state);
                    continue;
                }

                userSelection = inputValidation.Value;
            }

            return TieredMenu.GetMenuItem(userSelection - 1);
        }

        string ValidRangeOfMenuItemNumbers()
        {
            return $"Enter a menu item number (valid range 1-{TieredMenu.ItemCount}):";
        }

        void PopulateSelectRemoteServerMenuTier()
        {
            var selectRemoteServerTier = new MenuTier(string.Empty);

            if (!_state.RemoteServerSelected)
            {
                selectRemoteServerTier.MenuItems.Add(new SelectRemoteServerMenu(_state));
            }

            TieredMenu.AddTier(selectRemoteServerTier);
        }

        void PopulatePendingRequestsMenuTier()
        {
            var handleRequestsMenuTier = new MenuTier(Resources.MenuTierLabel_PendingRequests);

            if (!_state.LocalServer.NoFileTransfersPending)
            {
                handleRequestsMenuTier.MenuItems.Add(
                    new ProcessNextRequestInQueueMenuItem(_state));
            }

            if (_state.LocalServer.StalledTransferIds.Count > 0)
            {
                handleRequestsMenuTier.MenuItems.Add(
                    new RetryStalledFileTransferMenu(_state));
            }

            if (_state.LocalServer.UnreadTextMessageCount > 0)
            {
                foreach (var id in _state.LocalServer.TextSessionIdsWithUnreadMessages)
                {
                    var textSession = _state.LocalServer.GetTextSessionById(id).Value;

                    handleRequestsMenuTier.MenuItems.Add(
                        new ReadTextMessageMenuItem(_state, textSession));
                }
            }

            TieredMenu.AddTier(handleRequestsMenuTier);
        }

        void PopulateRemoteServerMenuTier()
        {
            var selectedServerMenuTier = new MenuTier(_state.RemoteServerInfo());

            if (_state.RemoteServerSelected)
            {
                selectedServerMenuTier.MenuItems.Add(new SendTextMessageMenuItem(_state));
                selectedServerMenuTier.MenuItems.Add(new SelectFileMenu(_state, true));
                selectedServerMenuTier.MenuItems.Add(new SelectFileMenu(_state, false));
                selectedServerMenuTier.MenuItems.Add(new EditServerInfoMenu(_state));
                selectedServerMenuTier.MenuItems.Add(new DeleteServerInfoMenuItem(_state));
                selectedServerMenuTier.MenuItems.Add(new SelectRemoteServerMenu(_state));
            }

            TieredMenu.AddTier(selectedServerMenuTier);
        }

        void PopulateViewLogsMenuTier()
        {
            var viewLogsMenuTier = new MenuTier(Resources.MenuTierLabel_ViewLogs);

            if (!_state.LocalServer.NoFileTransfers)
            {
                viewLogsMenuTier.MenuItems.Add(new FileTransferLogsMenu(_state));
            }

            if (!_state.LocalServer.NoTextSessions)
            {
                viewLogsMenuTier.MenuItems.Add(new TextMessageLogsMenu(_state));
            }

            if (!_state.LocalServer.NoRequests)
            {
                viewLogsMenuTier.MenuItems.Add(new ServerRequestLogsMenu(_state));
            }

            viewLogsMenuTier.MenuItems.Add(new ViewAllEventsMenuItem(_state));
            viewLogsMenuTier.MenuItems.Add(new SetLogLevelMenu(_state));

            TieredMenu.AddTier(viewLogsMenuTier);
        }

        void PopulateServerConfigurationMenuTier()
        {
            var serverConfigurationMenuTier =
                new MenuTier(Resources.MenuTierLabel_ServerConfiguration);

            serverConfigurationMenuTier.MenuItems.Add(new LocalServerSettingsMenu(_state));
            serverConfigurationMenuTier.MenuItems.Add(new SocketSettingsMenu(_state));
            serverConfigurationMenuTier.MenuItems.Add(new FileTransferSettingsMenu(_state));
            serverConfigurationMenuTier.MenuItems.Add(_shutdownServer);

            TieredMenu.AddTier(serverConfigurationMenuTier);
        }
    }
}
