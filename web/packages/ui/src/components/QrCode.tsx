// QR code for authenticator enrolment (slice S1.12a Build item 7; package uqr, MIT, approved 2026-11-20). Drawn as an
// inline SVG on the page: the enrolment secret never leaves the browser for an image service.
import { useMemo } from 'react';
import { encode } from 'uqr';

export function QrCode({ value, label }: { value: string; label: string }) {
  const { size, cells } = useMemo(() => {
    const { data } = encode(value, { ecc: 'M', border: 2 });
    const dark: [number, number][] = [];
    data.forEach((row, y) => row.forEach((on, x) => { if (on) dark.push([x, y]); }));
    return { size: data.length, cells: dark };
  }, [value]);
  return (
    <svg className="erp-qr" viewBox={`0 0 ${size} ${size}`} role="img" aria-label={label} shapeRendering="crispEdges">
      {cells.map(([x, y]) => <rect key={`${x}.${y}`} data-dark="" x={x} y={y} width={1} height={1} />)}
    </svg>
  );
}
