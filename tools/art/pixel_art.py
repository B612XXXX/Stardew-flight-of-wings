"""Connected-component labels and a schematic body guide for wing art."""
import numpy as np
from PIL import Image, ImageDraw

SIZE = (80, 80)


def components(mask, diagonal=False):
    """Label horizontal runs, joining only overlapping runs on adjacent rows."""
    labels = np.zeros(mask.shape, dtype=np.int32)
    parent = [0]
    previous = []

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for y, row in enumerate(mask):
        differences = np.diff(np.pad(row.astype(np.int8), (1, 1)))
        starts, ends = np.flatnonzero(differences == 1), np.flatnonzero(differences == -1)
        current, first = [], 0
        margin = int(diagonal)
        for start, end in zip(starts, ends):
            label = len(parent)
            parent.append(label)
            while first < len(previous) and previous[first][1] + margin <= start:
                first += 1
            cursor = first
            while cursor < len(previous) and previous[cursor][0] < end + margin:
                other = find(previous[cursor][2])
                root = find(label)
                if root != other:
                    parent[root] = other
                cursor += 1
            labels[y, start:end] = label
            current.append((start, end, label))
        previous = current
    lookup = np.array([find(i) for i in range(len(parent))], dtype=np.int32)
    labels = lookup[labels]
    counts = np.bincount(labels.ravel(), minlength=len(parent))
    return labels, counts


def guide(direction):
    im=Image.new('RGBA',SIZE)
    d=ImageDraw.Draw(im)
    # A deliberately schematic 16x32 measuring figure; not a replacement player.
    d.rectangle((35,30,44,38),fill='#bcbcbc',outline='#555555')
    d.rectangle((34,39,45,51),fill='#888888',outline='#555555')
    d.rectangle((33,40,34,49),fill='#aaaaaa')
    d.rectangle((45,40,46,49),fill='#aaaaaa')
    d.rectangle((35,52,38,61),fill='#666666')
    d.rectangle((41,52,44,61),fill='#666666')
    if direction=='right':d.rectangle((43,34,46,36),fill='#dddddd')
    elif direction=='left':d.rectangle((33,34,36,36),fill='#dddddd')
    elif direction=='front':
        d.point((37,34),fill='black');d.point((42,34),fill='black')
    return im
