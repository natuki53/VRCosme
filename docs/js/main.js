(() => {
  const navLinks = Array.from(document.querySelectorAll('.topnav a[href^="#"]'));
  const sections = navLinks
    .map((link) => document.querySelector(link.getAttribute('href')))
    .filter((section) => section instanceof HTMLElement);

  if (!navLinks.length || !sections.length) {
    return;
  }

  const setActiveLink = () => {
    const marker = window.scrollY + 140;
    let currentSectionId = sections[0].id;

    for (const section of sections) {
      if (section.offsetTop <= marker) {
        currentSectionId = section.id;
      }
    }

    for (const link of navLinks) {
      const isActive = link.getAttribute('href') === `#${currentSectionId}`;
      link.classList.toggle('is-active', isActive);
    }
  };

  setActiveLink();
  window.addEventListener('scroll', setActiveLink, { passive: true });
})();
