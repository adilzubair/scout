(() => {
  const demo = document.querySelector('#demo');
  if (!demo) return;

  const switcher = demo.querySelector('.demo-switcher');
  const buttons = [...demo.querySelectorAll('[data-example]')];
  const panels = [...demo.querySelectorAll('.demo-panel')];
  if (!switcher || buttons.length !== panels.length || !buttons.length) return;
  if (buttons.some(button => !panels.some(panel => panel.id === button.dataset.example))) return;

  const select = selected => {
    for (const button of buttons) button.setAttribute('aria-pressed', String(button === selected));
    for (const panel of panels) panel.hidden = panel.id !== selected.dataset.example;
  };

  for (const button of buttons) button.addEventListener('click', () => select(button));
  select(buttons[0]);
  demo.classList.add('demo-enhanced');
  switcher.hidden = false;

  const menu = document.querySelector('.mobile-menu');
  if (!menu) return;
  for (const link of menu.querySelectorAll('a')) {
    link.addEventListener('click', () => { menu.open = false; });
  }
  menu.addEventListener('keydown', event => {
    if (event.key === 'Escape' && menu.open) {
      menu.open = false;
      menu.querySelector('summary').focus();
    }
  });
})();
