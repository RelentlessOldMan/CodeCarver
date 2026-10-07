/* Book of Horrors #19: Duff's device, a switch jumping into a loop. Every step is a call. */
int duff_step(int i);
int duff(int count)
{
    int n = (count + 7) / 8, i = 0, sum = 0;
    switch (count % 8) {
    case 0: do { sum += duff_step(i++);
    case 7:      sum += duff_step(i++);
    case 6:      sum += duff_step(i++);
    case 5:      sum += duff_step(i++);
    case 4:      sum += duff_step(i++);
    case 3:      sum += duff_step(i++);
    case 2:      sum += duff_step(i++);
    case 1:      sum += duff_step(i++);
            } while (--n > 0);
    }
    return sum;
}
