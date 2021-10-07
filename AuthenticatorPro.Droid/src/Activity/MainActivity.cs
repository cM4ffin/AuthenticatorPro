// Copyright (C) 2021 jmh
// SPDX-License-Identifier: GPL-3.0-only

using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using AndroidX.CoordinatorLayout.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using AndroidX.Core.View;
using AndroidX.RecyclerView.Widget;
using AndroidX.Work;
using AuthenticatorPro.Droid.Adapter;
using AuthenticatorPro.Droid.Callback;
using AuthenticatorPro.Droid.Fragment;
using AuthenticatorPro.Droid.LayoutManager;
using AuthenticatorPro.Droid.Shared.Util;
using AuthenticatorPro.Droid.Util;
using AuthenticatorPro.Droid.Wear;
using AuthenticatorPro.Droid.Worker;
using AuthenticatorPro.Shared.Data;
using AuthenticatorPro.Shared.Data.Backup;
using AuthenticatorPro.Shared.Data.Backup.Converter;
using AuthenticatorPro.Shared.Entity;
using AuthenticatorPro.Shared.Persistence;
using AuthenticatorPro.Shared.Persistence.Exception;
using AuthenticatorPro.Shared.Service;
using AuthenticatorPro.Shared.View;
using Google.Android.Material.AppBar;
using Google.Android.Material.BottomAppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.Internal;
using Google.Android.Material.Snackbar;
using Java.Nio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xamarin.Essentials;
using ZXing;
using ZXing.Common;
using ZXing.Mobile;
using Configuration = Android.Content.Res.Configuration;
using Logger = AuthenticatorPro.Droid.Util.Logger;
using Result = Android.App.Result;
using SearchView = AndroidX.AppCompat.Widget.SearchView;
using Timer = System.Timers.Timer;
using Toolbar = AndroidX.AppCompat.Widget.Toolbar;
using Uri = Android.Net.Uri;

namespace AuthenticatorPro.Droid.Activity
{
    [Activity(Label = "@string/displayName", Theme = "@style/MainActivityTheme", MainLauncher = true,
        Icon = "@mipmap/ic_launcher", WindowSoftInputMode = SoftInput.AdjustPan,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataSchemes = new[] { "otpauth", "otpauth-migration" })]
    internal class MainActivity : AsyncActivity, IOnApplyWindowInsetsListener
    {
        private const int PermissionCameraCode = 0;

        private const int BackupReminderThresholdMinutes = 120;
        private const int ListPaddingBottom = 80;

        // Request codes
        private const int RequestUnlock = 0;
        private const int RequestRestore = 1;
        private const int RequestBackupFile = 2;
        private const int RequestBackupHtml = 3;
        private const int RequestBackupUriList = 4;
        private const int RequestQrCode = 5;
        private const int RequestCustomIcon = 6;
        private const int RequestSettingsRecreate = 7;
        private const int RequestImportAuthenticatorPlus = 8;
        private const int RequestImportAndOtp = 9;
        private const int RequestImportFreeOtpPlus = 10;
        private const int RequestImportAegis = 11;
        private const int RequestImportBitwarden = 12;
        private const int RequestImportWinAuth = 13;
        private const int RequestImportTotpAuthenticator = 14;
        private const int RequestImportUriList = 15;

        // Views
        private CoordinatorLayout _coordinatorLayout;
        private AppBarLayout _appBarLayout;
        private MaterialToolbar _toolbar;
        private ProgressBar _progressBar;
        private RecyclerView _authenticatorList;
        private FloatingActionButton _addButton;
        private BottomAppBar _bottomAppBar;

        private LinearLayout _emptyStateLayout;
        private TextView _emptyMessageText;
        private LinearLayout _startLayout;

        private AuthenticatorListAdapter _authenticatorListAdapter;
        private AutoGridLayoutManager _authenticatorLayout;
        private ReorderableListTouchHelperCallback _authenticatorTouchHelperCallback;

        // Data
        private readonly Database _database;

        private readonly ICategoryRepository _categoryRepository;
        private readonly IAuthenticatorCategoryRepository _authenticatorCategoryRepository;
        private readonly ICustomIconRepository _customIconRepository;

        private readonly IAuthenticatorCategoryService _authenticatorCategoryService;
        private readonly IAuthenticatorService _authenticatorService;
        private readonly IBackupService _backupService;
        private readonly ICustomIconService _customIconService;
        private readonly IImportService _importService;
        private readonly IRestoreService _restoreService;
        private readonly IQrCodeService _qrCodeService;

        private readonly IAuthenticatorView _authenticatorView;

        // State
        private readonly IIconResolver _iconResolver;
        private readonly ICustomIconDecoder _customIconDecoder;

        private readonly WearClient _wearClient;
        private PreferenceWrapper _preferences;

        private Timer _timer;
        private DateTime _pauseTime;
        private DateTime _lastBackupReminderTime;

        private bool _preventBackupReminder;
        private bool _updateOnResume;
        private bool _hasCreated;
        private bool _hasResumed;
        private bool _isWaitingForUnlock;
        private int _customIconApplyPosition;

        // Pause OnResume until unlock is complete
        private readonly SemaphoreSlim _unlockDatabaseLock;

        public MainActivity() : base(Resource.Layout.activityMain)
        {
            _iconResolver = Dependencies.Resolve<IIconResolver>();
            _customIconDecoder = Dependencies.Resolve<ICustomIconDecoder>();

            _wearClient = new WearClient(this);

            _unlockDatabaseLock = new SemaphoreSlim(0, 1);

            _database = Dependencies.Resolve<Database>();

            _authenticatorCategoryService = Dependencies.Resolve<IAuthenticatorCategoryService>();
            _categoryRepository = Dependencies.Resolve<ICategoryRepository>();
            _authenticatorCategoryRepository = Dependencies.Resolve<IAuthenticatorCategoryRepository>();
            _customIconRepository = Dependencies.Resolve<ICustomIconRepository>();

            _authenticatorService = Dependencies.Resolve<IAuthenticatorService>();
            _backupService = Dependencies.Resolve<IBackupService>();
            _customIconService = Dependencies.Resolve<ICustomIconService>();
            _importService = Dependencies.Resolve<IImportService>();
            _restoreService = Dependencies.Resolve<IRestoreService>();
            _qrCodeService = Dependencies.Resolve<IQrCodeService>();

            _authenticatorView = Dependencies.Resolve<IAuthenticatorView>();
        }

        #region Activity Lifecycle

        protected override async Task OnCreateAsync(Bundle savedInstanceState)
        {
            _hasCreated = true;

            Platform.Init(this, savedInstanceState);
            _preferences = new PreferenceWrapper(this);

            var windowFlags = WindowManagerFlags.Secure;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                if (_preferences.TransparentStatusBar)
                {
                    Window.SetStatusBarColor(Color.Transparent);
                }

                Window.SetDecorFitsSystemWindows(false);
                Window.SetNavigationBarColor(Color.Transparent);

                if (!IsDark)
                {
                    Window.InsetsController?.SetSystemBarsAppearance(
                        (int) WindowInsetsControllerAppearance.LightStatusBars,
                        (int) WindowInsetsControllerAppearance.LightStatusBars);
                }
            }
            else if (_preferences.TransparentStatusBar)
            {
                windowFlags |= WindowManagerFlags.TranslucentStatus;
            }

            Window.SetFlags(windowFlags, windowFlags);
            InitViews();

            if (savedInstanceState != null)
            {
                _pauseTime = new DateTime(savedInstanceState.GetLong("pauseTime"));
                _lastBackupReminderTime = new DateTime(savedInstanceState.GetLong("lastBackupReminderTime"));
            }
            else
            {
                _pauseTime = DateTime.MinValue;
                _lastBackupReminderTime = DateTime.MinValue;
            }

            if (_preferences.DefaultCategory != null)
            {
                _authenticatorView.CategoryId = _preferences.DefaultCategory;
            }

            _authenticatorView.SortMode = _preferences.SortMode;

            RunOnUiThread(InitAuthenticatorList);

            _timer = new Timer { Interval = 1000, AutoReset = true };

            _timer.Elapsed += delegate
            {
                RunOnUiThread(delegate
                {
                    _authenticatorListAdapter.Tick();
                });
            };

            _updateOnResume = true;

            if (_preferences.FirstLaunch)
            {
                StartActivity(typeof(IntroActivity));
            }

            await _wearClient.DetectCapability();
        }

        protected override async Task OnResumeAsync()
        {
            // Prevent double calls to onresume when unlocking database
            if (_hasResumed)
            {
                return;
            }

            _hasResumed = true;

            RunOnUiThread(delegate
            {
                // Perhaps the animation in onpause was cancelled
                _authenticatorList.Visibility = ViewStates.Invisible;
            });

            try
            {
                await UnlockIfRequired();
                _isWaitingForUnlock = false;
            }
            catch (Exception e)
            {
                Logger.Error($"Database not usable? error: {e}");
                ShowDatabaseErrorDialog(e);
                return;
            }

            // In case auto restore occurs when activity is loaded
            var autoRestoreCompleted = _preferences.AutoRestoreCompleted;
            _preferences.AutoRestoreCompleted = false;

            if (_updateOnResume || _hasCreated || autoRestoreCompleted)
            {
                _updateOnResume = false;
                await Update(_hasCreated);
                await CheckCategoryState();
            }
            else
            {
                _authenticatorView.Update();
                RunOnUiThread(delegate { _authenticatorListAdapter.Tick(); });
            }

            _hasCreated = false;
        }

        protected override async Task OnResumeDeferredAsync()
        {
            if (_isWaitingForUnlock)
            {
                return;
            }

            // Handle QR code scanning from intent
            if (Intent?.Data != null)
            {
                var uri = Intent.Data;
                Intent = null;
                await ParseQrCodeScanResult(uri.ToString());
            }

            CheckEmptyState();

            if (!_preventBackupReminder && _preferences.ShowBackupReminders &&
                (DateTime.UtcNow - _lastBackupReminderTime).TotalMinutes > BackupReminderThresholdMinutes)
            {
                RemindBackup();
            }

            _preventBackupReminder = false;
            TriggerAutoBackupWorker();

            await _wearClient.StartListening();
        }

        protected override void OnSaveInstanceState(Bundle outState)
        {
            base.OnSaveInstanceState(outState);
            outState.PutLong("pauseTime", _pauseTime.Ticks);
            outState.PutLong("lastBackupReminderTime", _lastBackupReminderTime.Ticks);
        }

        protected override async void OnPause()
        {
            base.OnPause();

            _timer?.Stop();
            _pauseTime = DateTime.UtcNow;

            if (!_isWaitingForUnlock)
            {
                _hasResumed = false;
            }

            RunOnUiThread(delegate
            {
                if (_authenticatorList != null)
                {
                    AnimUtil.FadeOutView(_authenticatorList, AnimUtil.LengthLong);
                }
            });

            _authenticatorView.Clear();
            await _wearClient.StopListening();
        }

        #endregion

        #region Activity Events

        protected override void
            OnActivityResultPreResume(int requestCode, [GeneratedEnum] Result resultCode, Intent intent)
        {
            _preventBackupReminder = true;

            if (requestCode == RequestUnlock)
            {
                if (resultCode != Result.Ok)
                {
                    FinishAffinity();
                    return;
                }

                _unlockDatabaseLock.Release();
                return;
            }

            if (resultCode == Result.Ok && requestCode == RequestSettingsRecreate)
            {
                Recreate();
            }
        }

        protected override async Task OnActivityResultAsync(int requestCode, [GeneratedEnum] Result resultCode,
            Intent intent)
        {
            if (resultCode != Result.Ok)
            {
                return;
            }

            switch (requestCode)
            {
                case RequestRestore:
                    await RestoreFromUri(intent.Data);
                    break;

                case RequestBackupFile:
                    await BackupToFile(intent.Data);
                    break;

                case RequestBackupHtml:
                    await BackupToHtmlFile(intent.Data);
                    break;

                case RequestBackupUriList:
                    await BackupToUriListFile(intent.Data);
                    break;

                case RequestCustomIcon:
                    await SetCustomIcon(intent.Data, _customIconApplyPosition);
                    break;

                case RequestQrCode:
                    await ScanQrCodeFromImage(intent.Data);
                    break;

                case RequestImportAuthenticatorPlus:
                    await ImportFromUri(new AuthenticatorPlusBackupConverter(_iconResolver), intent.Data);
                    break;

                case RequestImportAndOtp:
                    await ImportFromUri(new AndOtpBackupConverter(_iconResolver), intent.Data);
                    break;

                case RequestImportFreeOtpPlus:
                    await ImportFromUri(new FreeOtpPlusBackupConverter(_iconResolver), intent.Data);
                    break;

                case RequestImportAegis:
                    await ImportFromUri(new AegisBackupConverter(_iconResolver, _customIconDecoder), intent.Data);
                    break;

                case RequestImportBitwarden:
                    await ImportFromUri(new BitwardenBackupConverter(_iconResolver), intent.Data);
                    break;

                case RequestImportWinAuth:
                    await ImportFromUri(new WinAuthBackupConverter(_iconResolver), intent.Data);
                    break;

                case RequestImportTotpAuthenticator:
                    await ImportFromUri(new TotpAuthenticatorBackupConverter(_iconResolver), intent.Data);
                    break;

                case RequestImportUriList:
                    await ImportFromUri(new UriListBackupConverter(_iconResolver), intent.Data);
                    break;
            }
        }

        public override void OnConfigurationChanged(Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);

            // Force a relayout when the orientation changes
            Task.Run(async delegate
            {
                await Task.Delay(500);
                RunOnUiThread(_authenticatorListAdapter.NotifyDataSetChanged);
            });
        }

        public WindowInsetsCompat OnApplyWindowInsets(View view, WindowInsetsCompat insets)
        {
            var systemBarInsets = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());

            var layout = FindViewById<LinearLayout>(Resource.Id.toolbarWrapLayout);
            layout.SetPadding(0, systemBarInsets.Top, 0, 0);

            var bottomPadding = (int) ViewUtils.DpToPx(this, ListPaddingBottom) + systemBarInsets.Bottom;
            _authenticatorList.SetPadding(0, 0, 0, bottomPadding);

            return insets;
        }

        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            MenuInflater.Inflate(Resource.Menu.main, menu);

            var searchItem = menu.FindItem(Resource.Id.actionSearch);
            var searchView = (SearchView) searchItem.ActionView;
            searchView.QueryHint = GetString(Resource.String.search);

            searchView.QueryTextChange += (_, e) =>
            {
                var oldSearch = _authenticatorView.Search;

                _authenticatorView.Search = e.NewText;
                _authenticatorListAdapter.NotifyDataSetChanged();

                if (e.NewText == "")
                {
                    _authenticatorTouchHelperCallback.IsLocked = false;

                    if (!String.IsNullOrEmpty(oldSearch))
                    {
                        searchItem.CollapseActionView();
                    }
                }
                else
                {
                    _authenticatorTouchHelperCallback.IsLocked = true;
                }
            };

            return base.OnCreateOptionsMenu(menu);
        }

        public override bool OnMenuOpened(int featureId, IMenu menu)
        {
            var sortItemId = _authenticatorView.SortMode switch
            {
                SortMode.AlphabeticalAscending => Resource.Id.actionSortAZ,
                SortMode.AlphabeticalDescending => Resource.Id.actionSortZA,
                _ => Resource.Id.actionSortCustom
            };

            menu.FindItem(sortItemId)?.SetChecked(true);
            return base.OnMenuOpened(featureId, menu);
        }

        public override bool OnOptionsItemSelected(IMenuItem item)
        {
            SortMode sortMode;

            switch (item.ItemId)
            {
                case Resource.Id.actionSortAZ:
                    sortMode = SortMode.AlphabeticalAscending;
                    break;

                case Resource.Id.actionSortZA:
                    sortMode = SortMode.AlphabeticalDescending;
                    break;

                case Resource.Id.actionSortCustom:
                    sortMode = SortMode.Custom;
                    break;

                default:
                    return base.OnOptionsItemSelected(item);
            }

            if (_authenticatorView.SortMode == sortMode)
            {
                return false;
            }

            _authenticatorView.SortMode = sortMode;
            _preferences.SortMode = sortMode;
            _authenticatorListAdapter.NotifyDataSetChanged();
            item.SetChecked(true);

            return true;
        }

        private void OnBottomAppBarNavigationClick(object sender, Toolbar.NavigationClickEventArgs e)
        {
            var bundle = new Bundle();
            bundle.PutString("currentCategoryId", _authenticatorView.CategoryId);

            var fragment = new MainMenuBottomSheet { Arguments = bundle };
            fragment.CategoryClicked += async (_, id) =>
            {
                await SwitchCategory(id);
                RunOnUiThread(fragment.Dismiss);
            };

            fragment.BackupClicked += delegate
            {
                if (!_authenticatorView.AnyWithoutFilter())
                {
                    ShowSnackbar(Resource.String.noAuthenticators, Snackbar.LengthShort);
                    return;
                }

                OpenBackupMenu();
            };

            fragment.EditCategoriesClicked += delegate
            {
                _updateOnResume = true;
                StartActivity(typeof(EditCategoriesActivity));
            };

            fragment.SettingsClicked += delegate
            {
                StartActivityForResult(typeof(SettingsActivity), RequestSettingsRecreate);
            };

            fragment.AboutClicked += delegate
            {
                var sub = new AboutBottomSheet();

                sub.AboutClicked += delegate
                {
                    StartActivity(typeof(AboutActivity));
                };

                sub.RateClicked += delegate
                {
                    var intent = new Intent(Intent.ActionView, Uri.Parse("market://details?id=" + PackageName));

                    try
                    {
                        StartActivity(intent);
                    }
                    catch (ActivityNotFoundException)
                    {
                        Toast.MakeText(this, Resource.String.googlePlayNotInstalledError, ToastLength.Short).Show();
                    }
                };

                sub.ViewGitHubClicked += delegate
                {
                    var intent = new Intent(Intent.ActionView, Uri.Parse(Constants.GitHubRepo));

                    try
                    {
                        StartActivity(intent);
                    }
                    catch (ActivityNotFoundException)
                    {
                        Toast.MakeText(this, Resource.String.webBrowserMissing, ToastLength.Short).Show();
                    }
                };

                sub.Show(SupportFragmentManager, sub.Tag);
            };

            fragment.Show(SupportFragmentManager, fragment.Tag);
        }

        public override async void OnBackPressed()
        {
            var searchBarWasClosed = false;

            RunOnUiThread(delegate
            {
                var searchItem = _toolbar?.Menu.FindItem(Resource.Id.actionSearch);

                if (searchItem == null || !searchItem.IsActionViewExpanded)
                {
                    return;
                }

                searchItem.CollapseActionView();
                searchBarWasClosed = true;
            });

            if (searchBarWasClosed)
            {
                return;
            }

            var defaultCategory = _preferences.DefaultCategory;

            if (defaultCategory == null)
            {
                if (_authenticatorView.CategoryId != null)
                {
                    await SwitchCategory(null);
                    return;
                }
            }
            else
            {
                if (_authenticatorView.CategoryId != defaultCategory)
                {
                    await SwitchCategory(defaultCategory);
                    return;
                }
            }

            Finish();
        }

        public override async void OnRequestPermissionsResult(int requestCode, string[] permissions,
            [GeneratedEnum] Permission[] grantResults)
        {
            if (requestCode == PermissionCameraCode)
            {
                if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
                {
                    await ScanQrCodeFromCamera();
                }
                else
                {
                    ShowSnackbar(Resource.String.cameraPermissionError, Snackbar.LengthShort);
                }
            }

            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        }

        #endregion

        #region Database

        private async Task UnlockIfRequired()
        {
            switch (_database.IsOpen)
            {
                // Unlocked, no need to do anything
                case true:
                    return;

                // Locked and has password, wait for unlock in unlockactivity
                case false when _preferences.PasswordProtected:
                    _isWaitingForUnlock = true;
                    StartActivityForResult(typeof(UnlockActivity), RequestUnlock);
                    await _unlockDatabaseLock.WaitAsync();
                    break;

                // Locked but no password, unlock now
                case false:
                    await _database.Open(null);
                    break;
            }
        }

        private void ShowDatabaseErrorDialog(Exception exception)
        {
            var builder = new MaterialAlertDialogBuilder(this);
            builder.SetMessage(Resource.String.databaseError);
            builder.SetTitle(Resource.String.error);

            builder.SetNeutralButton(Resource.String.viewErrorLog, delegate
            {
                var intent = new Intent(this, typeof(ErrorActivity));
                intent.PutExtra("exception", exception.ToString());
                StartActivity(intent);
            });

            builder.SetPositiveButton(Resource.String.retry, async delegate
            {
                await _database.Close();
                Recreate();
            });

            builder.SetCancelable(false);
            builder.Create().Show();
        }

        #endregion

        #region Authenticator List

        private void InitViews()
        {
            _coordinatorLayout = FindViewById<CoordinatorLayout>(Resource.Id.coordinatorLayout);
            ViewCompat.SetOnApplyWindowInsetsListener(_coordinatorLayout, this);

            _toolbar = FindViewById<MaterialToolbar>(Resource.Id.toolbar);
            SetSupportActionBar(_toolbar);

            if (_preferences.DefaultCategory == null)
            {
                SupportActionBar.SetTitle(Resource.String.categoryAll);
            }
            else
            {
                SupportActionBar.SetDisplayShowTitleEnabled(false);
            }

            _appBarLayout = FindViewById<AppBarLayout>(Resource.Id.appBarLayout);
            _bottomAppBar = FindViewById<BottomAppBar>(Resource.Id.bottomAppBar);
            _bottomAppBar.NavigationClick += OnBottomAppBarNavigationClick;
            _bottomAppBar.MenuItemClick += delegate
            {
                if (_authenticatorListAdapter == null)
                {
                    return;
                }

                _toolbar.Menu.FindItem(Resource.Id.actionSearch).ExpandActionView();
                ScrollToPosition(0);
            };

            _progressBar = FindViewById<ProgressBar>(Resource.Id.appBarProgressBar);

            _addButton = FindViewById<FloatingActionButton>(Resource.Id.buttonAdd);
            _addButton.Click += OnAddButtonClick;

            _authenticatorList = FindViewById<RecyclerView>(Resource.Id.list);
            _emptyStateLayout = FindViewById<LinearLayout>(Resource.Id.layoutEmptyState);
            _emptyMessageText = FindViewById<TextView>(Resource.Id.textEmptyMessage);

            _startLayout = FindViewById<LinearLayout>(Resource.Id.layoutStart);

            var viewGuideButton = FindViewById<MaterialButton>(Resource.Id.buttonViewGuide);
            viewGuideButton.Click += delegate { StartActivity(typeof(GuideActivity)); };

            var importButton = FindViewById<MaterialButton>(Resource.Id.buttonImport);
            importButton.Click += delegate { OpenImportMenu(); };
        }

        private void InitAuthenticatorList()
        {
            var viewMode = ViewModeSpecification.FromName(_preferences.ViewMode);
            _authenticatorListAdapter =
                new AuthenticatorListAdapter(this, _authenticatorService, _authenticatorView, _customIconRepository,
                    viewMode, IsDark,
                    _preferences.TapToReveal) { HasStableIds = true };

            _authenticatorListAdapter.ItemClicked += OnAuthenticatorClicked;
            _authenticatorListAdapter.MenuClicked += OnAuthenticatorMenuClicked;
            _authenticatorListAdapter.MovementStarted += OnAuthenticatorListMovementStarted;
            _authenticatorListAdapter.MovementFinished += OnAuthenticatorListMovementFinished;

            _authenticatorList.SetAdapter(_authenticatorListAdapter);

            _authenticatorLayout = new AutoGridLayoutManager(this, viewMode.GetMinColumnWidth());
            _authenticatorList.SetLayoutManager(_authenticatorLayout);

            _authenticatorList.AddItemDecoration(new GridSpacingItemDecoration(this, _authenticatorLayout,
                viewMode.GetSpacing(), true));
            _authenticatorList.HasFixedSize = false;

            var animation = AnimationUtils.LoadLayoutAnimation(this, Resource.Animation.layout_animation_fall_down);
            _authenticatorList.LayoutAnimation = animation;

            _authenticatorTouchHelperCallback =
                new ReorderableListTouchHelperCallback(this, _authenticatorListAdapter, _authenticatorLayout);
            var touchHelper = new ItemTouchHelper(_authenticatorTouchHelperCallback);
            touchHelper.AttachToRecyclerView(_authenticatorList);
        }

        private void OnAuthenticatorListMovementStarted(object sender, EventArgs e)
        {
            _bottomAppBar.PerformHide();
        }

        private async void OnAuthenticatorListMovementFinished(object sender, EventArgs e)
        {
            _authenticatorView.CommitRanking();

            if (_authenticatorView.CategoryId == null)
            {
                await _authenticatorService.UpdateManyAsync(_authenticatorView);
            }
            else
            {
                var authenticatorCategories = _authenticatorView.GetCurrentBindings();
                await _authenticatorCategoryService.UpdateManyAsync(authenticatorCategories);
            }

            if (_preferences.SortMode != SortMode.Custom)
            {
                _preferences.SortMode = SortMode.Custom;
                _authenticatorView.SortMode = SortMode.Custom;
            }

            RunOnUiThread(_bottomAppBar.PerformShow);
            await _wearClient.NotifyChange();
        }

        private async Task Update(bool animateLayout)
        {
            var uiLock = new SemaphoreSlim(0, 1);
            var showingProgress = false;

            var loadTimer = new Timer(400) { Enabled = false, AutoReset = false };

            loadTimer.Elapsed += delegate
            {
                RunOnUiThread(delegate
                {
                    showingProgress = true;

                    AnimUtil.FadeInView(_progressBar, AnimUtil.LengthShort, false, delegate
                    {
                        if (uiLock.CurrentCount == 0)
                        {
                            uiLock.Release();
                        }
                    });
                });
            };

            var alreadyLoading = _progressBar.Visibility == ViewStates.Visible;

            if (!alreadyLoading)
            {
                loadTimer.Enabled = true;
            }

            await _authenticatorView.LoadFromPersistenceAsync();

            loadTimer.Enabled = false;

            if (showingProgress)
            {
                await uiLock.WaitAsync();
            }

            RunOnUiThread(delegate
            {
                _authenticatorListAdapter.NotifyDataSetChanged();
                _authenticatorListAdapter.Tick();

                if (animateLayout)
                {
                    _authenticatorList.ScheduleLayoutAnimation();
                }

                if (showingProgress || alreadyLoading)
                {
                    AnimUtil.FadeOutView(_progressBar, AnimUtil.LengthShort, true);
                }

                AnimUtil.FadeInView(_authenticatorList, AnimUtil.LengthShort, true, delegate
                {
                    uiLock.Release();
                });
            });

            await uiLock.WaitAsync();
        }

        private async Task CheckCategoryState()
        {
            if (_authenticatorView.CategoryId == null)
            {
                return;
            }

            var category = await _categoryRepository.GetAsync(_authenticatorView.CategoryId);

            if (category == null)
            {
                // Currently visible category has been deleted
                await SwitchCategory(null);
                return;
            }

            RunOnUiThread(delegate
            {
                SupportActionBar.SetDisplayShowTitleEnabled(true);
                SupportActionBar.Title = category.Name;
            });
        }

        private void CheckEmptyState()
        {
            if (!_authenticatorView.Any())
            {
                RunOnUiThread(delegate
                {
                    if (_emptyStateLayout.Visibility == ViewStates.Invisible)
                    {
                        AnimUtil.FadeInView(_emptyStateLayout, AnimUtil.LengthLong);
                    }

                    if (_authenticatorList.Visibility == ViewStates.Visible)
                    {
                        AnimUtil.FadeOutView(_authenticatorList, AnimUtil.LengthShort);
                    }

                    if (_authenticatorView.CategoryId == null)
                    {
                        _emptyMessageText.SetText(Resource.String.noAuthenticatorsHelp);
                        _startLayout.Visibility = ViewStates.Visible;
                    }
                    else
                    {
                        _emptyMessageText.SetText(Resource.String.noAuthenticatorsMessage);
                        _startLayout.Visibility = ViewStates.Gone;
                    }
                });

                _timer.Stop();
            }
            else
            {
                RunOnUiThread(delegate
                {
                    if (_emptyStateLayout.Visibility == ViewStates.Visible)
                    {
                        AnimUtil.FadeOutView(_emptyStateLayout, AnimUtil.LengthShort);
                    }

                    if (_authenticatorList.Visibility == ViewStates.Invisible)
                    {
                        AnimUtil.FadeInView(_authenticatorList, AnimUtil.LengthLong);
                    }

                    var firstVisiblePos = _authenticatorLayout.FindFirstCompletelyVisibleItemPosition();
                    var lastVisiblePos = _authenticatorLayout.FindLastCompletelyVisibleItemPosition();

                    var shouldShowOverscroll =
                        firstVisiblePos >= 0 && lastVisiblePos >= 0 &&
                        (firstVisiblePos > 0 || lastVisiblePos < _authenticatorView.Count - 1);

                    _authenticatorList.OverScrollMode =
                        shouldShowOverscroll ? OverScrollMode.Always : OverScrollMode.Never;

                    if (!shouldShowOverscroll)
                    {
                        ScrollToPosition(0);
                    }
                });

                _timer.Start();
            }
        }

        private async Task SwitchCategory(string id)
        {
            if (id == _authenticatorView.CategoryId)
            {
                CheckEmptyState();
                return;
            }

            string categoryName;

            if (id == null)
            {
                _authenticatorView.CategoryId = null;
                categoryName = GetString(Resource.String.categoryAll);
            }
            else
            {
                var category = await _categoryRepository.GetAsync(id);
                _authenticatorView.CategoryId = id;
                categoryName = category.Name;
            }

            CheckEmptyState();

            RunOnUiThread(delegate
            {
                SupportActionBar.Title = categoryName;
                _authenticatorListAdapter.NotifyDataSetChanged();
                _authenticatorList.ScheduleLayoutAnimation();
                ScrollToPosition(0, false);
            });
        }

        private void ScrollToPosition(int position, bool smooth = true)
        {
            if (position < 0 || position > _authenticatorView.Count - 1)
            {
                return;
            }

            if (smooth)
            {
                _authenticatorList.SmoothScrollToPosition(position);
            }
            else
            {
                _authenticatorList.ScrollToPosition(position);
            }

            _appBarLayout.SetExpanded(true);
        }

        private void OnAuthenticatorClicked(object sender, int position)
        {
            var auth = _authenticatorView[position];
            var clipboard = (ClipboardManager) GetSystemService(ClipboardService);
            var clip = ClipData.NewPlainText("code", auth.GetCode());
            clipboard.PrimaryClip = clip;

            ShowSnackbar(Resource.String.copiedToClipboard, Snackbar.LengthShort);
        }

        private void OnAuthenticatorMenuClicked(object sender, int position)
        {
            var auth = _authenticatorView[position];
            var bundle = new Bundle();
            bundle.PutInt("type", (int) auth.Type);
            bundle.PutLong("counter", auth.Counter);

            var fragment = new AuthenticatorMenuBottomSheet { Arguments = bundle };

            fragment.RenameClicked += delegate { OpenRenameDialog(position); };
            fragment.ChangeIconClicked += delegate { OpenIconDialog(position); };
            fragment.AssignCategoriesClicked += async delegate { await OpenCategoriesDialog(position); };
            fragment.ShowQrCodeClicked += delegate { OpenQrCodeDialog(position); };
            fragment.DeleteClicked += delegate { OpenDeleteDialog(position); };

            fragment.Show(SupportFragmentManager, fragment.Tag);
        }

        private void OpenQrCodeDialog(int position)
        {
            var auth = _authenticatorView[position];
            string uri;

            try
            {
                uri = auth.GetOtpAuthUri();
            }
            catch (NotSupportedException)
            {
                ShowSnackbar(Resource.String.qrCodeNotSupported, Snackbar.LengthShort);
                return;
            }

            var bundle = new Bundle();
            bundle.PutString("uri", uri);

            var fragment = new QrCodeBottomSheet { Arguments = bundle };
            fragment.Show(SupportFragmentManager, fragment.Tag);
        }

        private void OpenDeleteDialog(int position)
        {
            var auth = _authenticatorView[position];

            var builder = new MaterialAlertDialogBuilder(this);
            builder.SetMessage(Resource.String.confirmAuthenticatorDelete);
            builder.SetTitle(Resource.String.warning);
            builder.SetPositiveButton(Resource.String.delete, async delegate
            {
                try
                {
                    await _authenticatorService.DeleteWithCategoryBindingsAsync(auth);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                    ShowSnackbar(Resource.String.genericError, Snackbar.LengthShort);
                    return;
                }

                try
                {
                    await _customIconService.CullUnused();
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                    // ignored
                }

                await _authenticatorView.LoadFromPersistenceAsync();
                RunOnUiThread(delegate { _authenticatorListAdapter.NotifyItemRemoved(position); });
                CheckEmptyState();

                _preferences.BackupRequired = BackupRequirement.WhenPossible;
                await _wearClient.NotifyChange();
            });

            builder.SetNegativeButton(Resource.String.cancel, delegate { });
            builder.SetCancelable(true);

            var dialog = builder.Create();
            dialog.Show();
        }

        private void OnAddButtonClick(object sender, EventArgs e)
        {
            var fragment = new AddMenuBottomSheet();
            fragment.QrCodeClicked += delegate
            {
                var subFragment = new ScanQrCodeBottomSheet();
                subFragment.FromCameraClicked += async delegate { await RequestPermissionThenScanQrCode(); };
                subFragment.FromGalleryClicked += delegate { StartFilePickActivity("image/*", RequestQrCode); };
                subFragment.Show(SupportFragmentManager, subFragment.Tag);
            };

            fragment.EnterKeyClicked += OpenAddDialog;
            fragment.RestoreClicked += delegate
            {
                StartFilePickActivity(Backup.MimeType, RequestRestore);
            };

            fragment.ImportClicked += delegate
            {
                OpenImportMenu();
            };

            fragment.Show(SupportFragmentManager, fragment.Tag);
        }

        #endregion

        #region QR Code Scanning

        private async Task ScanQrCodeFromCamera()
        {
            var options = new MobileBarcodeScanningOptions
            {
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                TryHarder = true,
                AutoRotate = true
            };

            var overlay = LayoutInflater.Inflate(Resource.Layout.scanOverlay, null);

            var scanner = new MobileBarcodeScanner { UseCustomOverlay = true, CustomOverlay = overlay };

            var flashButton = overlay.FindViewById<MaterialButton>(Resource.Id.buttonFlash);
            flashButton.Click += delegate
            {
                scanner.ToggleTorch();
            };

            var hasFlashlight = PackageManager.HasSystemFeature(PackageManager.FeatureCameraFlash);
            flashButton.Visibility = hasFlashlight ? ViewStates.Visible : ViewStates.Gone;

            _preventBackupReminder = true;
            var result = await scanner.Scan(options);

            if (result == null)
            {
                return;
            }

            await ParseQrCodeScanResult(result.Text);
        }

        private async Task ScanQrCodeFromImage(Uri uri)
        {
            Bitmap bitmap;

            try
            {
                var data = await FileUtil.ReadFile(this, uri);
                bitmap = await BitmapFactory.DecodeByteArrayAsync(data, 0, data.Length);
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowSnackbar(Resource.String.filePickError, Snackbar.LengthShort);
                return;
            }

            if (bitmap == null)
            {
                ShowSnackbar(Resource.String.filePickError, Snackbar.LengthShort);
                return;
            }

            var reader = new BarcodeReader<Bitmap>(null, null, ls => new GlobalHistogramBinarizer(ls))
            {
                AutoRotate = true,
                TryInverted = true,
                Options = new DecodingOptions
                {
                    PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }, TryHarder = true
                }
            };

            ZXing.Result result;

            try
            {
                var buffer = ByteBuffer.Allocate(bitmap.ByteCount);
                await bitmap.CopyPixelsToBufferAsync(buffer);
                buffer.Rewind();

                var bytes = new byte[buffer.Remaining()];
                buffer.Get(bytes);

                var source = new RGBLuminanceSource(bytes, bitmap.Width, bitmap.Height,
                    RGBLuminanceSource.BitmapFormat.RGBA32);
                result = reader.Decode(source);
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowSnackbar(Resource.String.genericError, Snackbar.LengthShort);
                return;
            }

            if (result == null)
            {
                ShowSnackbar(Resource.String.qrCodeFormatError, Snackbar.LengthShort);
                return;
            }

            await ParseQrCodeScanResult(result.Text);
        }

        private async Task ParseQrCodeScanResult(string uri)
        {
            if (uri.StartsWith("otpauth-migration"))
            {
                await OnOtpAuthMigrationScan(uri);
            }
            else if (uri.StartsWith("otpauth"))
            {
                await OnOtpAuthScan(uri);
            }
            else
            {
                ShowSnackbar(Resource.String.qrCodeFormatError, Snackbar.LengthShort);
                return;
            }

            _preferences.BackupRequired = BackupRequirement.Urgent;
            await _wearClient.NotifyChange();
        }

        private async Task OnOtpAuthScan(string uri)
        {
            Authenticator auth;

            try
            {
                auth = await _qrCodeService.ParseOtpAuthUri(uri);
            }
            catch (ArgumentException)
            {
                ShowSnackbar(Resource.String.qrCodeFormatError, Snackbar.LengthShort);
                return;
            }
            catch (EntityDuplicateException)
            {
                ShowSnackbar(Resource.String.duplicateAuthenticator, Snackbar.LengthShort);
                return;
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowSnackbar(Resource.String.genericError, Snackbar.LengthShort);
                return;
            }

            if (_authenticatorView.CategoryId != null)
            {
                var category = await _categoryRepository.GetAsync(_authenticatorView.CategoryId);
                await _authenticatorCategoryService.AddAsync(auth, category);
            }

            await _authenticatorView.LoadFromPersistenceAsync();
            CheckEmptyState();

            var position = _authenticatorView.IndexOf(auth);

            RunOnUiThread(delegate
            {
                _authenticatorListAdapter.NotifyItemInserted(position);
                ScrollToPosition(position);
            });

            ShowSnackbar(Resource.String.scanSuccessful, Snackbar.LengthShort);
        }

        private async Task OnOtpAuthMigrationScan(string uri)
        {
            int added;

            try
            {
                added = await _qrCodeService.ParseOtpMigrationUri(uri);
            }
            catch (ArgumentException)
            {
                ShowSnackbar(Resource.String.qrCodeFormatError, Snackbar.LengthShort);
                return;
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowSnackbar(Resource.String.genericError, Snackbar.LengthShort);
                return;
            }

            await _authenticatorView.LoadFromPersistenceAsync();
            await SwitchCategory(null);
            RunOnUiThread(_authenticatorListAdapter.NotifyDataSetChanged);

            var message = String.Format(GetString(Resource.String.restoredFromMigration), added);
            ShowSnackbar(message, Snackbar.LengthLong);
        }

        private async Task RequestPermissionThenScanQrCode()
        {
            if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.Camera) != Permission.Granted)
            {
                ActivityCompat.RequestPermissions(this, new[] { Manifest.Permission.Camera }, PermissionCameraCode);
            }
            else
            {
                await ScanQrCodeFromCamera();
            }
        }

        #endregion

        #region Restore / Import

        private void OpenImportMenu()
        {
            var fragment = new ImportBottomSheet();
            fragment.GoogleAuthenticatorClicked += delegate
            {
                StartWebBrowserActivity(Constants.GitHubRepo + "/wiki/Importing-from-Google-Authenticator");
            };

            // Use */* mime-type for most binary files because some files might not show on older Android versions
            // Use */* for json also, because application/json doesn't work

            fragment.AuthenticatorPlusClicked += delegate
            {
                StartFilePickActivity("*/*", RequestImportAuthenticatorPlus);
            };

            fragment.AndOtpClicked += delegate
            {
                StartFilePickActivity("*/*", RequestImportAndOtp);
            };

            fragment.FreeOtpPlusClicked += delegate
            {
                StartFilePickActivity("*/*", RequestImportFreeOtpPlus);
            };

            fragment.AegisClicked += delegate
            {
                StartFilePickActivity("*/*", RequestImportAegis);
            };

            fragment.BitwardenClicked += delegate
            {
                StartFilePickActivity("*/*", RequestImportBitwarden);
            };

            fragment.WinAuthClicked += delegate
            {
                StartFilePickActivity("text/plain", RequestImportWinAuth);
            };

            fragment.AuthyClicked += delegate
            {
                StartWebBrowserActivity(Constants.GitHubRepo + "/wiki/Importing-from-Authy");
            };

            fragment.TotpAuthenticatorClicked += delegate
            {
                StartFilePickActivity("*/*", RequestImportTotpAuthenticator);
            };

            fragment.BlizzardAuthenticatorClicked += delegate
            {
                StartWebBrowserActivity(Constants.GitHubRepo + "/wiki/Importing-from-Blizzard-Authenticator");
            };

            fragment.SteamClicked += delegate
            {
                StartWebBrowserActivity(Constants.GitHubRepo + "/wiki/Importing-from-Steam");
            };

            fragment.UriListClicked += delegate
            {
                StartFilePickActivity("*/*", RequestImportUriList);
            };

            fragment.Show(SupportFragmentManager, fragment.Tag);
        }

        private async Task RestoreFromUri(Uri uri)
        {
            byte[] data;

            try
            {
                data = await FileUtil.ReadFile(this, uri);
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowSnackbar(Resource.String.filePickError, Snackbar.LengthShort);
                return;
            }

            if (data.Length == 0)
            {
                ShowSnackbar(Resource.String.invalidFileError, Snackbar.LengthShort);
                return;
            }

            async Task<RestoreResult> DecryptAndRestore(string password)
            {
                Backup backup = null;

                await Task.Run(delegate
                {
                    backup = Backup.FromBytes(data, password);
                });

                return await _restoreService.RestoreAndUpdateAsync(backup);
            }

            if (Backup.IsReadableWithoutPassword(data))
            {
                RestoreResult result;
                RunOnUiThread(delegate { _progressBar.Visibility = ViewStates.Visible; });

                try
                {
                    result = await DecryptAndRestore(null);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                    ShowSnackbar(Resource.String.invalidFileError, Snackbar.LengthShort);
                    return;
                }
                finally
                {
                    RunOnUiThread(delegate { _progressBar.Visibility = ViewStates.Invisible; });
                }

                await FinaliseRestore(result);
                return;
            }

            var bundle = new Bundle();
            bundle.PutInt("mode", (int) BackupPasswordBottomSheet.Mode.Enter);
            var sheet = new BackupPasswordBottomSheet { Arguments = bundle };

            sheet.PasswordEntered += async (_, password) =>
            {
                sheet.SetBusyText(Resource.String.decrypting);

                try
                {
                    var result = await DecryptAndRestore(password);
                    sheet.Dismiss();
                    await FinaliseRestore(result);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                    sheet.Error = GetString(Resource.String.restoreError);
                    sheet.SetBusyText(null);
                }
            };

            sheet.Show(SupportFragmentManager, sheet.Tag);
        }

        private async Task ImportFromUri(BackupConverter converter, Uri uri)
        {
            byte[] data;

            try
            {
                data = await FileUtil.ReadFile(this, uri);
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowSnackbar(Resource.String.filePickError, Snackbar.LengthShort);
                return;
            }

            async Task ConvertAndRestore(string password)
            {
                var result = await _importService.ImportAsync(converter, data, password);
                await FinaliseRestore(result);
                _preferences.BackupRequired = BackupRequirement.Urgent;
            }

            void ShowPasswordSheet()
            {
                var bundle = new Bundle();
                bundle.PutInt("mode", (int) BackupPasswordBottomSheet.Mode.Enter);
                var sheet = new BackupPasswordBottomSheet { Arguments = bundle };

                sheet.PasswordEntered += async (_, password) =>
                {
                    sheet.SetBusyText(Resource.String.decrypting);

                    try
                    {
                        await ConvertAndRestore(password);
                        sheet.Dismiss();
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e);
                        sheet.Error = GetString(Resource.String.restoreError);
                        sheet.SetBusyText(null);
                    }
                };
                sheet.Show(SupportFragmentManager, sheet.Tag);
            }

            switch (converter.PasswordPolicy)
            {
                case BackupConverter.BackupPasswordPolicy.Never:
                    RunOnUiThread(delegate { _progressBar.Visibility = ViewStates.Visible; });

                    try
                    {
                        await ConvertAndRestore(null);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e);
                        ShowSnackbar(Resource.String.importError, Snackbar.LengthShort);
                    }
                    finally
                    {
                        RunOnUiThread(delegate { _progressBar.Visibility = ViewStates.Invisible; });
                    }

                    break;

                case BackupConverter.BackupPasswordPolicy.Always:
                    ShowPasswordSheet();
                    break;

                case BackupConverter.BackupPasswordPolicy.Maybe:
                    try
                    {
                        await ConvertAndRestore(null);
                    }
                    catch
                    {
                        ShowPasswordSheet();
                    }

                    break;
            }
        }

        private async Task FinaliseRestore(RestoreResult result)
        {
            ShowSnackbar(result.ToString(this), Snackbar.LengthShort);

            if (result.IsVoid())
            {
                return;
            }

            await _authenticatorView.LoadFromPersistenceAsync();
            await SwitchCategory(null);

            RunOnUiThread(delegate
            {
                _authenticatorListAdapter.NotifyDataSetChanged();
                _authenticatorList.ScheduleLayoutAnimation();
            });

            await _wearClient.NotifyChange();
        }

        #endregion

        #region Backup

        private void OpenBackupMenu()
        {
            var fragment = new BackupBottomSheet();

            void ShowPicker(string mimeType, int requestCode, string fileExtension)
            {
                StartFileSaveActivity(mimeType, requestCode,
                    $"backup-{DateTime.Now:yyyy-MM-dd_HHmmss}.{fileExtension}");
            }

            fragment.BackupFileClicked += delegate
            {
                ShowPicker(Backup.MimeType, RequestBackupFile, Backup.FileExtension);
            };

            fragment.BackupHtmlFileClicked += delegate
            {
                ShowPicker(HtmlBackup.MimeType, RequestBackupHtml, HtmlBackup.FileExtension);
            };

            fragment.BackupUriListClicked += delegate
            {
                ShowPicker(UriListBackup.MimeType, RequestBackupUriList, UriListBackup.FileExtension);
            };

            fragment.Show(SupportFragmentManager, fragment.Tag);
        }

        private async Task BackupToFile(Uri destination)
        {
            async Task DoBackup(string password)
            {
                var backup = await _backupService.CreateBackupAsync();

                try
                {
                    byte[] data = null;
                    await Task.Run(delegate { data = backup.ToBytes(password); });
                    await FileUtil.WriteFile(this, destination, data);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                    ShowSnackbar(Resource.String.genericError, Snackbar.LengthShort);
                    return;
                }

                FinaliseBackup();
            }

            if (_preferences.PasswordProtected && _preferences.DatabasePasswordBackup)
            {
                var password = await SecureStorageWrapper.GetDatabasePassword();
                await DoBackup(password);
                return;
            }

            var bundle = new Bundle();
            bundle.PutInt("mode", (int) BackupPasswordBottomSheet.Mode.Set);
            var fragment = new BackupPasswordBottomSheet { Arguments = bundle };

            fragment.PasswordEntered += async (sender, password) =>
            {
                var busyText = !String.IsNullOrEmpty(password) ? Resource.String.encrypting : Resource.String.saving;
                fragment.SetBusyText(busyText);
                await DoBackup(password);
                ((BackupPasswordBottomSheet) sender).Dismiss();
            };

            fragment.CancelClicked += (sender, _) =>
            {
                // TODO: Delete empty file only if we just created it
                // DocumentsContract.DeleteDocument(ContentResolver, uri);
                ((BackupPasswordBottomSheet) sender).Dismiss();
            };

            fragment.Show(SupportFragmentManager, fragment.Tag);
        }

        private async Task BackupToHtmlFile(Uri destination)
        {
            try
            {
                var backup = await _backupService.CreateHtmlBackupAsync();
                await FileUtil.WriteFile(this, destination, backup.ToString());
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowSnackbar(Resource.String.genericError, Snackbar.LengthShort);
                return;
            }

            FinaliseBackup();
        }

        private async Task BackupToUriListFile(Uri destination)
        {
            try
            {
                var backup = await _backupService.CreateUriListBackupAsync();
                await FileUtil.WriteFile(this, destination, backup.ToString());
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowSnackbar(Resource.String.genericError, Snackbar.LengthShort);
                return;
            }

            FinaliseBackup();
        }

        private void FinaliseBackup()
        {
            _preferences.BackupRequired = BackupRequirement.NotRequired;
            ShowSnackbar(Resource.String.saveSuccess, Snackbar.LengthLong);
        }

        private void RemindBackup()
        {
            if (!_authenticatorView.AnyWithoutFilter())
            {
                return;
            }

            if (_preferences.BackupRequired != BackupRequirement.Urgent || _preferences.AutoBackupEnabled)
            {
                return;
            }

            _lastBackupReminderTime = DateTime.UtcNow;
            var snackbar = Snackbar.Make(_coordinatorLayout, Resource.String.backupReminder, Snackbar.LengthLong);
            snackbar.SetAnchorView(_addButton);
            snackbar.SetAction(Resource.String.backupNow, delegate
            {
                OpenBackupMenu();
            });

            var callback = new SnackbarCallback();
            callback.Dismissed += (_, e) =>
            {
                if (e == Snackbar.Callback.DismissEventSwipe)
                {
                    _preferences.BackupRequired = BackupRequirement.NotRequired;
                }
            };

            snackbar.AddCallback(callback);
            snackbar.Show();
        }

        #endregion

        #region Add Dialog

        private void OpenAddDialog(object sender, EventArgs e)
        {
            var fragment = new AddAuthenticatorBottomSheet();
            fragment.AddClicked += OnAddDialogSubmit;
            fragment.Show(SupportFragmentManager, fragment.Tag);
        }

        private async void OnAddDialogSubmit(object sender, Authenticator auth)
        {
            var dialog = (AddAuthenticatorBottomSheet) sender;

            try
            {
                if (_authenticatorView.CategoryId == null)
                {
                    await _authenticatorService.AddAsync(auth);
                }
                else
                {
                    await _authenticatorService.AddAsync(auth);

                    var category = await _categoryRepository.GetAsync(_authenticatorView.CategoryId);
                    await _authenticatorCategoryService.AddAsync(auth, category);
                }
            }
            catch (EntityDuplicateException)
            {
                dialog.SecretError = GetString(Resource.String.duplicateAuthenticator);
                return;
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowSnackbar(Resource.String.genericError, Snackbar.LengthShort);
                return;
            }

            await _authenticatorView.LoadFromPersistenceAsync();
            CheckEmptyState();

            var position = _authenticatorView.IndexOf(auth);

            RunOnUiThread(delegate
            {
                _authenticatorListAdapter.NotifyItemInserted(position);
                ScrollToPosition(position);
            });

            dialog.Dismiss();
            _preferences.BackupRequired = BackupRequirement.Urgent;

            await _wearClient.NotifyChange();
        }

        #endregion

        #region Rename Dialog

        private void OpenRenameDialog(int position)
        {
            var auth = _authenticatorView[position];

            var bundle = new Bundle();
            bundle.PutInt("position", position);
            bundle.PutString("issuer", auth.Issuer);
            bundle.PutString("username", auth.Username);

            var fragment = new RenameAuthenticatorBottomSheet { Arguments = bundle };
            fragment.RenameClicked += OnRenameDialogSubmit;
            fragment.Show(SupportFragmentManager, fragment.Tag);
        }

        private async void OnRenameDialogSubmit(object sender, RenameAuthenticatorBottomSheet.RenameEventArgs args)
        {
            var auth = _authenticatorView[args.ItemPosition];

            try
            {
                await _authenticatorService.RenameAsync(auth, args.Issuer, args.Username);
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowSnackbar(Resource.String.genericError, Snackbar.LengthShort);
                return;
            }

            RunOnUiThread(delegate { _authenticatorListAdapter.NotifyItemChanged(args.ItemPosition); });
            _preferences.BackupRequired = BackupRequirement.WhenPossible;
            await _wearClient.NotifyChange();
        }

        #endregion

        #region Icon Dialog

        private void OpenIconDialog(int position)
        {
            var bundle = new Bundle();
            bundle.PutInt("position", position);

            var fragment = new ChangeIconBottomSheet { Arguments = bundle };
            fragment.IconSelected += OnIconDialogIconSelected;
            fragment.UseCustomIconClick += delegate
            {
                _customIconApplyPosition = position;
                StartFilePickActivity("image/*", RequestCustomIcon);
            };
            fragment.Show(SupportFragmentManager, fragment.Tag);
        }

        private async void OnIconDialogIconSelected(object sender, ChangeIconBottomSheet.IconSelectedEventArgs args)
        {
            var auth = _authenticatorView[args.ItemPosition];
            var oldIcon = auth.Icon;

            try
            {
                await _authenticatorService.SetIconAsync(auth, args.Icon);
            }
            catch (Exception e)
            {
                Logger.Error(e);
                auth.Icon = oldIcon;
                ShowSnackbar(Resource.String.genericError, Snackbar.LengthShort);
                return;
            }

            _preferences.BackupRequired = BackupRequirement.WhenPossible;
            RunOnUiThread(delegate { _authenticatorListAdapter.NotifyItemChanged(args.ItemPosition); });
            await _wearClient.NotifyChange();

            ((ChangeIconBottomSheet) sender).Dismiss();
        }

        #endregion

        #region Custom Icons

        private async Task SetCustomIcon(Uri source, int position)
        {
            CustomIcon icon;

            try
            {
                var data = await FileUtil.ReadFile(this, source);
                icon = await _customIconDecoder.Decode(data);
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowSnackbar(Resource.String.filePickError, Snackbar.LengthShort);
                return;
            }

            var auth = _authenticatorView[position];
            var oldIcon = auth.Icon;

            try
            {
                await _authenticatorService.SetCustomIconAsync(auth, icon);
            }
            catch (Exception e)
            {
                Logger.Error(e);
                auth.Icon = oldIcon;
                ShowSnackbar(Resource.String.genericError, Snackbar.LengthShort);
                return;
            }

            _preferences.BackupRequired = BackupRequirement.WhenPossible;

            RunOnUiThread(delegate { _authenticatorListAdapter.NotifyItemChanged(position); });
            await _wearClient.NotifyChange();
        }

        #endregion

        #region Categories

        private async Task OpenCategoriesDialog(int position)
        {
            var auth = _authenticatorView[position];
            var authenticatorCategories = await _authenticatorCategoryRepository.GetAllForAuthenticatorAsync(auth);
            var categoryIds = authenticatorCategories.Select(ac => ac.CategoryId).ToArray();

            var bundle = new Bundle();
            bundle.PutInt("position", position);
            bundle.PutStringArray("assignedCategoryIds", categoryIds);

            var fragment = new AssignCategoriesBottomSheet { Arguments = bundle };
            fragment.CategoryClicked += OnCategoriesDialogCategoryClicked;
            fragment.EditCategoriesClicked += delegate
            {
                _updateOnResume = true;
                StartActivity(typeof(EditCategoriesActivity));
                fragment.Dismiss();
            };
            fragment.Closed += OnCategoriesDialogClosed;
            fragment.Show(SupportFragmentManager, fragment.Tag);
        }

        private async void OnCategoriesDialogClosed(object sender, EventArgs e)
        {
            await _authenticatorView.LoadFromPersistenceAsync();

            if (_authenticatorView.CategoryId == null)
            {
                return;
            }

            _authenticatorListAdapter.NotifyDataSetChanged();
            CheckEmptyState();
        }

        private async void OnCategoriesDialogCategoryClicked(object sender,
            AssignCategoriesBottomSheet.CategoryClickedEventArgs args)
        {
            var auth = _authenticatorView[args.ItemPosition];
            var category = await _categoryRepository.GetAsync(args.CategoryId);

            try
            {
                if (args.IsChecked)
                {
                    await _authenticatorCategoryService.AddAsync(auth, category);
                }
                else
                {
                    await _authenticatorCategoryService.RemoveAsync(auth, category);
                }
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowSnackbar(Resource.String.genericError, Snackbar.LengthShort);
            }
        }

        #endregion

        #region Misc

        private void ShowSnackbar(int textRes, int length)
        {
            var snackbar = Snackbar.Make(_coordinatorLayout, textRes, length);
            snackbar.SetAnchorView(_addButton);
            snackbar.Show();
        }

        private void ShowSnackbar(string message, int length)
        {
            var snackbar = Snackbar.Make(_coordinatorLayout, message, length);
            snackbar.SetAnchorView(_addButton);
            snackbar.Show();
        }

        private void StartFilePickActivity(string mimeType, int requestCode)
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType(mimeType);

            BaseApplication.PreventNextLock = true;

            try
            {
                StartActivityForResult(intent, requestCode);
            }
            catch (ActivityNotFoundException)
            {
                ShowSnackbar(Resource.String.filePickerMissing, Snackbar.LengthLong);
            }
        }

        private void StartFileSaveActivity(string mimeType, int requestCode, string fileName)
        {
            var intent = new Intent(Intent.ActionCreateDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType(mimeType);
            intent.PutExtra(Intent.ExtraTitle, fileName);

            BaseApplication.PreventNextLock = true;

            try
            {
                StartActivityForResult(intent, requestCode);
            }
            catch (ActivityNotFoundException)
            {
                ShowSnackbar(Resource.String.filePickerMissing, Snackbar.LengthLong);
            }
        }

        private void StartWebBrowserActivity(string url)
        {
            var intent = new Intent(Intent.ActionView, Uri.Parse(url));

            try
            {
                StartActivity(intent);
            }
            catch (ActivityNotFoundException)
            {
                ShowSnackbar(Resource.String.webBrowserMissing, Snackbar.LengthLong);
            }
        }

        private void TriggerAutoBackupWorker()
        {
            if (!_preferences.AutoBackupEnabled && !_preferences.AutoRestoreEnabled)
            {
                return;
            }

            var request = new OneTimeWorkRequest.Builder(typeof(AutoBackupWorker)).Build();
            var manager = WorkManager.GetInstance(this);
            manager.EnqueueUniqueWork(AutoBackupWorker.Name, ExistingWorkPolicy.Replace, request);
        }

        #endregion
    }
}