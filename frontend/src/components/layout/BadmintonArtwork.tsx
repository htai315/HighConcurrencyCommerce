export function BadmintonArtwork() {
  return <svg className="badminton-artwork" viewBox="0 0 560 500" fill="none" aria-hidden="true">
    <defs>
      <pattern id="racket-strings" width="13" height="13" patternUnits="userSpaceOnUse"><path d="M13 0H0V13" stroke="#e0ecff" strokeWidth=".9" opacity=".65"/></pattern>
      <linearGradient id="racket-frame" x1="0" y1="0" x2="1" y2="1"><stop stopColor="#fff"/><stop offset=".45" stopColor="#bccffb"/><stop offset="1" stopColor="#7095df"/></linearGradient>
      <linearGradient id="court-floor" x1="0" y1="0" x2="0" y2="1"><stop stopColor="#4c78ed"/><stop offset="1" stopColor="#1646c9"/></linearGradient>
      <filter id="equipment-shadow" x="-70%" y="-60%" width="240%" height="230%"><feDropShadow dx="13" dy="24" stdDeviation="12" floodColor="#08237a" floodOpacity=".5"/></filter>
    </defs>
    <path d="m40 290 280-120 210 190-278 116Z" fill="url(#court-floor)" opacity=".55"/>
    <g stroke="#a8c4ff" strokeWidth="1.4" opacity=".4"><path d="m40 290 280-120 210 190-278 116Z"/><path d="m80 328 280-120M210 217l210 190M163 401l278-120M102 264l210 190"/></g>
    <g transform="translate(98 22) rotate(-27 145 225)" filter="url(#equipment-shadow)">
      <path d="M144 241v139" stroke="#c9d9fd" strokeWidth="8"/><path d="M144 340v97" stroke="#c5f176" strokeWidth="18" strokeLinecap="round"/>
      {[0,1,2,3,4,5,6,7].map(i => <path key={i} d={`M136 ${350+i*11}l16-5`} stroke="#7ba741" strokeWidth="2"/>)}
      <ellipse cx="144" cy="140" rx="91" ry="118" fill="#1546bc" stroke="#a0beff" strokeWidth="12"/>
      <ellipse cx="144" cy="140" rx="87" ry="114" fill="url(#racket-strings)" stroke="url(#racket-frame)" strokeWidth="5"/>
      <path d="m119 253 25 29 24-29" stroke="#e9f1ff" strokeWidth="5"/>
      <path d="M68 91c11-30 33-51 63-57" stroke="white" strokeWidth="3" strokeLinecap="round" opacity=".7"/>
    </g>
    <g transform="translate(380 115) rotate(28)" filter="url(#equipment-shadow)">
      {[-48,-32,-16,0,16,32,48].map((angle,i) => <g key={angle} transform={`rotate(${angle} 0 110)`}>
        <path d="M-7 93-13 16Q0-7 13 16L7 93Z" fill={i%2 ? '#ecf1ff' : '#fff'} stroke="#b9ccee" strokeWidth="1.3"/>
        <path d="M0 14v82" stroke="#c0cee6"/>
      </g>)}
      <path d="M-22 90q22 8 44 0l-4 15h-36Z" fill="#defb9c"/>
      <path d="M-18 105h36v15a18 18 0 0 1-36 0Z" fill="#fff"/>
      <path d="M-17 107h34" stroke="#b8cbe8" strokeWidth="2"/>
    </g>
    <path d="M390 35q75 25 99 101M410 20q70 26 92 80" stroke="#c8f57e" strokeWidth="2" strokeLinecap="round" opacity=".7"/>
  </svg>
}

