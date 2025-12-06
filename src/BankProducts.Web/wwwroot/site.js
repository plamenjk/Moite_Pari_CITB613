(function(){
  const key='theme'; const saved=localStorage.getItem(key) || 'dark';
  if(saved==='light') document.documentElement.classList.add('light');
  document.addEventListener('click', (e)=>{
    if(e.target && e.target.id==='themeToggle'){
      const isLight=document.documentElement.classList.toggle('light');
      localStorage.setItem(key, isLight? 'light' : 'dark');
    }
  });
  function setupWatchlist(){
    document.querySelectorAll('[data-watch][data-id]').forEach(btn=>{
      btn.addEventListener('click', ()=>{
        const scope = btn.getAttribute('data-watch');
        const id = btn.getAttribute('data-id');
        const k = 'watch_'+scope;
        const arr = JSON.parse(localStorage.getItem(k) || '[]');
        if(!arr.includes(id)){ arr.push(id); localStorage.setItem(k, JSON.stringify(arr)); btn.textContent='★ Запазен'; }
      });
    });
  }
  document.addEventListener('DOMContentLoaded', setupWatchlist);
})();

