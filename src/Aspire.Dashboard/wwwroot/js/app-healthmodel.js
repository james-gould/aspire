import './d3.v7.min.js';

export function createHealthModelGraph(svg, interop) {
    return new HealthModelGraph(svg, interop);
}

class HealthModelGraph {
    constructor(svg, interop) {
        this.svg = d3.select(svg);
        this.viewport = this.svg.select('.health-model-viewport');
        this.interop = interop;
        this.positions = new Map();
        this.editable = false;
        this.active = null;
        this.disposed = false;
        this.manuallyFramed = false;
        this.zoom = d3.zoom().scaleExtent([0.05, 3]).on('zoom', event => {
            this.viewport.attr('transform', event.transform);
            if (event.sourceEvent) this.manuallyFramed = true;
        });
        this.svg.call(this.zoom).on('dblclick.zoom', null);
        this.observer = new ResizeObserver(() => this.resize());
        this.observer.observe(svg.parentElement);
        this.drag = d3.drag()
            .filter(event => this.editable && !event.button && !event.ctrlKey && !this.active)
            .subject((event, position) => position)
            .clickDistance(3)
            .on('start', (event) => {
                this.active = event.subject;
                this.moved = false;
            })
            .on('drag', event => {
                if (this.disposed || this.active !== event.subject) return;
                this.moved = true;
                this.active.x = event.x;
                this.active.y = event.y;
                this.drawPositions();
            })
            .on('end', () => this.finishDrag());
        this.blur = () => {
            if (this.active) {
                d3.select(window).on('.drag', null);
                d3.dragEnable(window, true);
                this.finishDrag();
            }
        };
        window.addEventListener('blur', this.blur);
        this.resize();
    }

    update(positions, editable) {
        this.editable = editable;
        const topologyChanged = positions.length !== this.positions.size ||
            positions.some(position => !this.positions.has(position.name));
        const next = new Map();
        for (const value of positions) {
            const current = this.positions.get(value.name) || { name: value.name };
            if (current !== this.active) Object.assign(current, value);
            next.set(value.name, current);
        }
        this.positions = next;
        this.svg.selectAll('.health-model-entity').each((_, index, elements) => {
            const element = elements[index];
            d3.select(element).datum(this.positions.get(element.dataset.entity))
                .call(this.drag)
                .on('keydown.health-model', event => {
                    const position = this.positions.get(element.dataset.entity);
                    if (!position) return;
                    if (event.key === 'Enter' || event.key === ' ') {
                        event.preventDefault();
                        this.interop.invokeMethodAsync('SelectEntity', position.name);
                    } else if (this.editable && ['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.key)) {
                        event.preventDefault();
                        const step = event.shiftKey ? 64 : 8;
                        const x = position.x + (event.key === 'ArrowRight' ? step : event.key === 'ArrowLeft' ? -step : 0);
                        const y = position.y + (event.key === 'ArrowDown' ? step : event.key === 'ArrowUp' ? -step : 0);
                        this.interop.invokeMethodAsync('MoveEntity', position.name, x, y);
                    }
                });
        });
        this.drawPositions();
        if (topologyChanged && !this.manuallyFramed) this.fit();
    }

    async finishDrag() {
        const position = this.active;
        this.active = null;
        if (position && this.moved && !this.disposed) {
            this.manuallyFramed = true;
            await this.interop.invokeMethodAsync('MoveEntity', position.name, position.x, position.y);
        }
    }

    drawPositions() {
        this.svg.selectAll('.health-model-entity').attr('transform', position => `translate(${position.x},${position.y})`);
        this.svg.selectAll('.health-model-edge').attr('d', (_, index, elements) => {
            const edge = elements[index];
            const parent = this.positions.get(edge.dataset.parent);
            const child = this.positions.get(edge.dataset.child);
            const y1 = parent.y + 52, y2 = child.y - 52, middle = (y1 + y2) / 2;
            return `M ${parent.x} ${y1} C ${parent.x} ${middle}, ${child.x} ${middle}, ${child.x} ${y2}`;
        });
    }

    resize() {
        const container = this.svg.node().parentElement;
        if (!container.clientWidth || !container.clientHeight) return;
        this.svg.attr('viewBox', `0 0 ${container.clientWidth} ${container.clientHeight}`);
        if (!this.manuallyFramed && this.positions.size) this.fit();
    }

    fit() {
        if (!this.positions.size) return;
        const container = this.svg.node().parentElement;
        if (!container.clientWidth || !container.clientHeight) return;
        const values = [...this.positions.values()];
        const minX = Math.min(...values.map(p => p.x)) - 140;
        const minY = Math.min(...values.map(p => p.y)) - 84;
        const width = Math.max(...values.map(p => p.x)) + 140 - minX;
        const height = Math.max(...values.map(p => p.y)) + 84 - minY;
        const scale = Math.min(1, container.clientWidth / width, container.clientHeight / height);
        this.manuallyFramed = false;
        const transform = d3.zoomIdentity
            .translate(container.clientWidth / 2, container.clientHeight / 2)
            .scale(scale).translate(-minX - width / 2, -minY - height / 2);
        this.svg.call(this.zoom.transform, transform);
    }

    zoomBy(factor) {
        this.manuallyFramed = true;
        this.svg.call(this.zoom.scaleBy, factor);
    }

    dispose() {
        this.disposed = true;
        this.observer.disconnect();
        window.removeEventListener('blur', this.blur);
        if (this.active) {
            d3.select(window).on('.drag', null);
            d3.dragEnable(window, true);
        }
        this.svg.on('.zoom', null);
        this.svg.selectAll('.health-model-entity').on('.drag', null).on('.health-model', null);
    }
}
