// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

document.querySelectorAll('[data-password-toggle]').forEach((button) => {
  button.addEventListener('click', () => {
    const wrapper = button.closest('.account-password-wrap');
    const input = wrapper?.querySelector('input');
    if (!(input instanceof HTMLInputElement)) return;

    const passwordIsVisible = input.type === 'text';
    input.type = passwordIsVisible ? 'password' : 'text';
    button.setAttribute('aria-pressed', String(!passwordIsVisible));
    button.setAttribute('aria-label', passwordIsVisible ? 'Hiện mật khẩu' : 'Ẩn mật khẩu');
    wrapper.classList.toggle('is-password-visible', !passwordIsVisible);
    input.focus({ preventScroll: true });
  });
});
