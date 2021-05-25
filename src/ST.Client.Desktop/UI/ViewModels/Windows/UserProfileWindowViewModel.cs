using ReactiveUI;
using System.Application.Entities;
using System.Application.Models;
using System.Application.Services;
using System.Application.UI.Resx;
using System.Linq;
using System.Linq.Expressions;
using System.Properties;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace System.Application.UI.ViewModels
{
    public partial class UserProfileWindowViewModel : WindowViewModel
    {
        byte[]? userInfoValue;
        UserInfoDTO? userInfoSource;
        readonly IUserManager userManager;
        public UserProfileWindowViewModel() : base()
        {
            Title = ThisAssembly.AssemblyTrademark + " | " + AppResources.UserProfile;
            userManager = DI.Get<IUserManager>();
            Initialize();
            async void Initialize()
            {
                this.InitAreas();
                userInfoSource = await userManager.GetCurrentUserInfoAsync() ?? new UserInfoDTO();
                userInfoValue = Serializable.SMP(userInfoSource);

                // (SetFields)DTO => VM
                _NickName = userInfoSource.NickName;
                _Gender = userInfoSource.Gender;
                _BirthDate = userInfoSource.GetBirthDate();
                if (userInfoSource.AreaId.HasValue) this.SetAreaId(userInfoSource.AreaId.Value);

                userInfoSource = Serializable.DMP<UserInfoDTO>(userInfoValue) ?? throw new ArgumentNullException(nameof(userInfoSource));
                _CurrentPhoneNumber = await userManager.GetCurrentUserPhoneNumberAsync();
                if (string.IsNullOrWhiteSpace(_CurrentPhoneNumber)) _CurrentPhoneNumber = AppResources.Unbound;

                // IsModifySubscribe
                void SubscribeOnNext<T>(T value, Action<T> onNext)
                {
                    onNext(value);
                    var currentUserInfoValue = Serializable.SMP(userInfoSource);
                    IsModify = !Enumerable.SequenceEqual(userInfoValue, currentUserInfoValue);
                }
                void SubscribeFormItem<T>(Expression<Func<UserProfileWindowViewModel, T>> expression, Action<T> onNext) => this.WhenAnyValue(expression).Subscribe(value => SubscribeOnNext(value, onNext)).AddTo(this);
                SubscribeFormItem(x => x.NickName, x => userInfoSource.NickName = x ?? string.Empty);
                SubscribeFormItem(x => x.Gender, x => userInfoSource.Gender = x);
                SubscribeFormItem(x => x.BirthDate, x => userInfoSource.SetBirthDate(x));
                void SubscribeAreaOnNext(IArea? area)
                {
                    var areaId = area?.Id;
                    userInfoSource.AreaId = (!areaId.HasValue ||
                        areaId.Value == AreaUIHelper.PleaseSelect.Id) ?
                        this.GetSelectAreaId() : areaId;
                }
                SubscribeFormItem(x => x.AreaSelectItem2, SubscribeAreaOnNext);
                SubscribeFormItem(x => x.AreaSelectItem3, SubscribeAreaOnNext);
                SubscribeFormItem(x => x.AreaSelectItem4, SubscribeAreaOnNext);
            }
            OnBindFastLoginClick = ReactiveCommand.CreateFromTask<string>(async channel_ =>
            {
                if (Enum.TryParse<FastLoginChannel>(channel_, out var channel))
                {
                    await OnBindFastLoginClickAsync(channel);
                }
            });
            OnUnbundleFastLoginClick = ReactiveCommand.CreateFromTask<string>(async channel_ =>
            {
                if (Enum.TryParse<FastLoginChannel>(channel_, out var channel))
                {
                    await OnUnbundleFastLoginClickAsync(channel);
                }
            });
        }

        bool _IsModify;
        public bool IsModify
        {
            get => _IsModify;
            set => this.RaiseAndSetIfChanged(ref _IsModify, value);
        }

        bool _IsLoading;
        public bool IsLoading
        {
            get => _IsLoading;
            set => this.RaiseAndSetIfChanged(ref _IsLoading, value);
        }

        string? _NickName;
        public string? NickName
        {
            get => _NickName;
            set => this.RaiseAndSetIfChanged(ref _NickName, value);
        }

        string? _CurrentPhoneNumber;
        public string? CurrentPhoneNumber
        {
            get => _CurrentPhoneNumber;
            set => this.RaiseAndSetIfChanged(ref _CurrentPhoneNumber, value);
        }

        Gender _Gender;
        public Gender Gender
        {
            get => _Gender;
            set => this.RaiseAndSetIfChanged(ref _Gender, value);
        }

        DateTimeOffset? _BirthDate;
        public DateTimeOffset? BirthDate
        {
            get => _BirthDate;
            set => this.RaiseAndSetIfChanged(ref _BirthDate, value);
        }

        public bool IsComplete { get; set; }

        public async void Submit()
        {
            if (!IsModify || IsLoading) return;

            if (userInfoSource == null) throw new ArgumentNullException(nameof(userInfoSource));

            IsLoading = true;

            var request = new EditUserProfileRequest
            {
                NickName = NickName ?? string.Empty,
                Avatar = userInfoSource.Avatar,
                Gender = Gender,
                BirthDate = userInfoSource.BirthDate,
                BirthDateTimeZone = userInfoSource.BirthDateTimeZone,
                AreaId = this.GetSelectAreaId(),
            };

            var response = await ICloudServiceClient.Instance.Manage.EditUserProfile(request);
            if (response.IsSuccess)
            {
                await userManager.SetCurrentUserInfoAsync(userInfoSource, true);
                await UserService.Current.RefreshUserAsync();

                IsComplete = true;
                var msg = AppResources.Success_.Format(AppResources.User_EditProfile);
                Toast.Show(msg);
                Close?.Invoke();
            }

            IsLoading = false;
        }

        public Action? Close { private get; set; }

        /// <summary>
        /// 换绑手机号码 按钮点击
        /// </summary>
        public ICommand OnBtnChangeBindPhoneNumberClick { get; } = ReactiveCommand.Create(() =>
        {
            UserService.Current.ShowWindow(CustomWindow.ChangeBindPhoneNumber);
        });

        /// <summary>
        /// 绑定手机号码 按钮点击
        /// </summary>
        public ICommand OnBtnBindPhoneNumberClick { get; } = ReactiveCommand.Create(() =>
        {
            UserService.Current.ShowWindow(CustomWindow.BindPhoneNumber);
        });

        /// <summary>
        /// 绑定用于快速登录的第三方账号 按钮点击
        /// </summary>
        public ICommand OnBindFastLoginClick { get; }

        async Task OnBindFastLoginClickAsync(FastLoginChannel channel)
        {
            await LoginOrRegisterWindowViewModel.FastLoginOrRegisterAsync(null, channel, isBind: true);
        }

        /// <summary>
        /// 解绑用于快速登录的第三方账号 按钮点击
        /// </summary>
        public ICommand OnUnbundleFastLoginClick { get; }

        async Task OnUnbundleFastLoginClickAsync(FastLoginChannel channel)
        {
            if (!UserService.Current.HasPhoneNumber)
            {
                return;
            }

            if (IsLoading) return;

            var r = await MessageBoxCompat.ShowAsync(AppResources.User_UnbundleAccountTip, ThisAssembly.AssemblyTrademark, MessageBoxButtonCompat.OKCancel);
            if (r == MessageBoxResultCompat.OK)
            {
                IsLoading = true;

                var response = await ICloudServiceClient.Instance.Manage.UnbundleAccount(channel);

                if (response.IsSuccess)
                {
                    await UserService.Current.UnbundleAccountAfterUpdateAsync(channel);
                    var msg = AppResources.Success_.Format(AppResources.Unbundling);
                    Toast.Show(msg);
                }

                IsLoading = false;
            }
        }
    }
}