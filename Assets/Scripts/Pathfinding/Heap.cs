using System;
using System.Collections;
using System.Collections.Generic;

public class Heap<T> where T : IHeapItem<T>
{
    T[] items;
    int currentItemCount;

    public Heap(int maxHeapSize)
    {
        items = new T[maxHeapSize];
    }

    public void Add(T item)
    {
        item.HeapIndex = currentItemCount;
        items[currentItemCount] = item;
        SortUp(item);
        currentItemCount++;
    }

    public T RemoveFirst()
    {
        T first = items[0];
        currentItemCount--;
        items[0] = items[currentItemCount];
        items[0].HeapIndex = 0;
        SortDown(items[0]);
        return first;
    }

    public void UpdateItem(T item)
    {
        SortUp(item);
    }

    public int Count => currentItemCount;

    public bool Contains(T item)
    {
        // Defensive: item.HeapIndex may contain stale value from previous searches.
        if (item == null) return false;
        int idx = item.HeapIndex;
        if (idx < 0 || idx >= currentItemCount) return false;
        T found = items[idx];
        if (found == null) return false;
        return EqualityComparer<T>.Default.Equals(found, item);
    }

    void SortDown(T item)
    {
        while (true)
        {
            int left = item.HeapIndex * 2 + 1;
            int right = item.HeapIndex * 2 + 2;
            int swapIndex = 0;

            if (left < currentItemCount)
            {
                swapIndex = left;

                if (right < currentItemCount)
                {
                    if (items[left].CompareTo(items[right]) < 0)
                        swapIndex = right;
                }

                if (item.CompareTo(items[swapIndex]) < 0)
                    Swap(item, items[swapIndex]);
                else
                    return;
            }
            else
                return;
        }
    }

    void SortUp(T item)
    {
        int parentIndex = (item.HeapIndex - 1) / 2;

        while (true)
        {
            if (parentIndex < 0) break;
            T parent = items[parentIndex];
            if (parent == null) break;
            if (item.CompareTo(parent) > 0)
            {
                Swap(item, parent);
            }
            else break;

            parentIndex = (item.HeapIndex - 1) / 2;
        }
    }

    void Swap(T a, T b)
    {
        items[a.HeapIndex] = b;
        items[b.HeapIndex] = a;

        int temp = a.HeapIndex;
        a.HeapIndex = b.HeapIndex;
        b.HeapIndex = temp;
    }
}

public interface IHeapItem<T> : IComparable<T>
{
    int HeapIndex { get; set; }
}