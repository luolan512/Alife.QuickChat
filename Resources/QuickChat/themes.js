// Theme presets change paint only. The default leaves all existing CSS and custom colours intact.
(() => {
  const palettes = {
    dawn: { scheme:'light', panel:'#FBF6ED', sidebar:'#F3EAD7', rail:'#EADDC0', surface:'#FFFCF7', input:'#FFFCF6', text:'#3D342B', muted:'#7C6C5B', border:'#DDCFBA', accent:'#B94E18', onAccent:'#FFFFFF', selected:'#F7DEC4', user:'#FAE2CC', userText:'#633515', hover:'#F1E3D0', process:'#9C6738', scroll:'#C6B49A', danger:'#B73332' },
    qqnt: { scheme:'light', panel:'#F7F9FC', sidebar:'#F0F3F8', rail:'#E7EDF5', surface:'#FFFFFF', input:'#FFFFFF', text:'#243248', muted:'#65748B', border:'#D7E0EC', accent:'#0066D9', onAccent:'#FFFFFF', selected:'#E2EDFF', user:'#DBEAFF', userText:'#153B70', hover:'#E9EFF8', process:'#3978C9', scroll:'#B4C1D3', danger:'#C03142' },
    navy: { scheme:'dark', panel:'#0E192B', sidebar:'#111F35', rail:'#172842', surface:'#1B2C45', input:'#17263D', text:'#E2EAF7', muted:'#9BABCA', border:'#344863', accent:'#8C9EFF', onAccent:'#151D38', selected:'#2B3C60', user:'#30467A', userText:'#F0F3FF', hover:'#263A57', process:'#9AAEFF', scroll:'#526787', danger:'#FF9FA3' },
    obsidian: { scheme:'dark', panel:'#191B1F', sidebar:'#202227', rail:'#292C31', surface:'#2B2D33', input:'#24262C', text:'#EBE7E0', muted:'#B0A99B', border:'#49473F', accent:'#D9B976', onAccent:'#292113', selected:'#40392C', user:'#463C29', userText:'#F5E8C8', hover:'#36342F', process:'#D1B780', scroll:'#716A5D', danger:'#F0A09B' }
  };
  const names = { '默认主题':'default', '温暖晨曦':'dawn', '晴空映雪':'qqnt', 'QQNT蓝白':'qqnt', '活力薄荷':'qqnt', '星际幽蓝（动态）':'navy', '星际幽蓝':'navy', '极夜碳黑':'obsidian', default:'default', dawn:'dawn', qqnt:'qqnt', mint:'qqnt', navy:'navy', obsidian:'obsidian' };
  let sky = null;
  const between = (min, max) => min + Math.random() * (max - min);
  function meteorPlan() {
    const roll = Math.random();
    const shower = roll < .01;
    const multiple = !shower && roll < .10;
    return { shower, count: shower ? Math.floor(between(7, 13)) : multiple ? Math.floor(between(2, 5)) : 1,
      spacing: shower ? between(90, 190) : between(80, 280), next: between(3000, 12000) };
  }
  function stopSky() {
    if (!sky) return;
    sky.dispose(); sky = null;
  }
  function startSky() {
    if (sky || !document.querySelector) return;
    const web = document.documentElement.dataset.qcSurface === 'web';
    const target = document.querySelector(web ? '#messages' : '#mainColumn');
    if (!target) return;
    const canvas = document.createElement('canvas');
    canvas.className = 'qc-star-sky'; canvas.setAttribute('aria-hidden', 'true');
    (web ? document.body : target).appendChild(canvas);
    const ctx = canvas.getContext('2d');
    if (!ctx) { canvas.remove(); return; }
    let width = 0, height = 0, frame = 0, previous = -Infinity, stopped = false;
    let nextMeteor = performance.now() + between(3000, 12000);
    const stars = [], meteors = [], pending = [];
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)');
    const makeStar = () => {
      const base = between(.20, .56);
      return { x: Math.random(), y: Math.random(), radius: between(.45, .8),
        base, peak: between(Math.max(.60, base + .12), .96), next: performance.now() + between(0, 6000),
        start: -Infinity, duration: between(700, 2300) };
    };
    function resize() {
      const rect = target.getBoundingClientRect();
      width = Math.max(1, rect.width); height = Math.max(1, rect.height);
      const dpr = Math.min(window.devicePixelRatio || 1, 1.5);
      canvas.width = Math.round(width * dpr); canvas.height = Math.round(height * dpr);
      if (web) { canvas.style.left = rect.left + 'px'; canvas.style.top = rect.top + 'px';
        canvas.style.width = width + 'px'; canvas.style.height = height + 'px'; }
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      const count = Math.min(160, Math.max(24, Math.round(width * height / 2000)));
      while (stars.length < count) stars.push(makeStar());
      stars.length = count;
      paint(performance.now(), true);
    }
    function addMeteor(now) {
      const angle = between(.55, .95), speed = between(180, 310);
      meteors.push({ x: between(.25, 1.05) * width, y: between(-.03, .6) * height,
        vx: -Math.cos(angle) * speed, vy: Math.sin(angle) * speed,
        start: now, duration: between(420, 850), tail: between(20, 38), alpha: between(.82, 1), width: between(1.1, 1.65) });
    }
    function paint(now, staticOnly = false) {
      ctx.clearRect(0, 0, width, height);
      for (const star of stars) {
        if (!staticOnly && now >= star.next) {
          star.start = now; star.duration = between(700, 2300); star.peak = between(Math.max(.60, star.base + .12), .96);
          star.next = now + star.duration + between(600, 6500);
        }
        const progress = (now - star.start) / star.duration;
        const pulse = !staticOnly && progress >= 0 && progress <= 1 ? Math.sin(progress * Math.PI) ** 2 : 0;
        ctx.fillStyle = `rgba(185,207,255,${star.base + pulse * (star.peak - star.base)})`;
        ctx.beginPath(); ctx.arc(star.x * width, star.y * height, star.radius, 0, Math.PI * 2); ctx.fill();
      }
      if (staticOnly) return;
      if (now >= nextMeteor) {
        const plan = meteorPlan();
        for (let i = 0; i < plan.count; i++) pending.push(now + i * plan.spacing);
        nextMeteor = now + plan.next;
      }
      while (pending.length && pending[0] <= now) { pending.shift(); if (meteors.length < 10) addMeteor(now); }
      for (let i = meteors.length - 1; i >= 0; i--) {
        const meteor = meteors[i], age = now - meteor.start;
        if (age >= meteor.duration) { meteors.splice(i, 1); continue; }
        const x = meteor.x + meteor.vx * age / 1000, y = meteor.y + meteor.vy * age / 1000;
        const speed = Math.hypot(meteor.vx, meteor.vy);
        const tx = x - meteor.vx / speed * meteor.tail, ty = y - meteor.vy / speed * meteor.tail;
        const alpha = meteor.alpha * Math.sin(age / meteor.duration * Math.PI);
        const gradient = ctx.createLinearGradient(tx, ty, x, y);
        gradient.addColorStop(0, 'rgba(175,202,255,0)'); gradient.addColorStop(1, `rgba(235,244,255,${alpha})`);
        ctx.strokeStyle = gradient; ctx.lineWidth = meteor.width; ctx.beginPath(); ctx.moveTo(tx, ty); ctx.lineTo(x, y); ctx.stroke();
        // A compact bright head reads as a meteor against softer background stars.
        ctx.fillStyle = `rgba(248,252,255,${alpha})`;
        ctx.beginPath(); ctx.arc(x, y, meteor.width * .65, 0, Math.PI * 2); ctx.fill();
      }
    }
    function tick(now) {
      if (stopped || document.hidden || reduced.matches) return;
      if (now - previous >= 40) { paint(now); previous = now; }
      frame = requestAnimationFrame(tick);
    }
    function resume() {
      cancelAnimationFrame(frame);
      meteors.length = 0; pending.length = 0;
      nextMeteor = performance.now() + between(3000, 12000);
      if (!document.hidden && !reduced.matches) frame = requestAnimationFrame(tick);
      else paint(performance.now(), true);
    }
    const observer = new ResizeObserver(resize); observer.observe(target);
    document.addEventListener('visibilitychange', resume);
    reduced.addEventListener('change', resume);
    sky = { dispose() { stopped = true; cancelAnimationFrame(frame); observer.disconnect();
      document.removeEventListener('visibilitychange', resume); reduced.removeEventListener('change', resume); canvas.remove(); } };
    resize(); resume();
  }
  window.QuickChatThemes = {
    apply(name) {
      const root = document.documentElement;
      const key = names[name] || 'default';
      root.dataset.qcTheme = key;
      for (const token of Object.keys(palettes.dawn)) root.style.removeProperty('--qc-' + token);
      if (palettes[key]) for (const [token, value] of Object.entries(palettes[key])) root.style.setProperty('--qc-' + token, value);
      if (key === 'navy') startSky(); else stopSky();
      return key;
    }
  };
})();
